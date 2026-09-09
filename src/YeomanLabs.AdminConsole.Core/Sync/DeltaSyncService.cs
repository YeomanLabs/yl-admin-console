using System.Text.Json;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Sync;

/// <summary>
/// Keeps the SQLite cache current (doc 03 §Cache &amp; sync). Users, groups (with <c>members@delta</c>) and Entra devices
/// use Graph delta queries; managed devices, SKUs and the organization are re-read on an interval. Never touches the UI thread.
/// The App hosts <see cref="RunLoopAsync"/> in a BackgroundService; tests call the per-entity methods directly.
/// </summary>
public sealed class DeltaSyncService : ISyncStatus
{
    private static readonly CacheEntity[] DeltaEntities = [CacheEntity.Users, CacheEntity.Groups, CacheEntity.Devices];

    private readonly GraphClient _graph;
    private readonly CacheDb _db;
    private readonly SyncOptions _options;
    private readonly ILogger<DeltaSyncService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly Dictionary<CacheEntity, SyncProgress> _latest = new();
    private readonly object _latestLock = new();
    private TaskCompletionSource _wake = NewWake();

    public DeltaSyncService(GraphClient graph, CacheDb db, SyncOptions options, ILogger<DeltaSyncService> logger, TimeProvider? time = null)
    {
        _graph = graph;
        _db = db;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<SyncProgress>? ProgressChanged;

    public bool IsInitialSyncComplete => DeltaEntities.All(e => _db.GetSyncState(e)?.DeltaLink is not null);
    public bool IsRunning { get; private set; }
    public DateTimeOffset? LastCycleCompleted { get; private set; }
    public Exception? LastError { get; private set; }

    public IReadOnlyDictionary<CacheEntity, SyncProgress> Latest
    {
        get { lock (_latestLock) return new Dictionary<CacheEntity, SyncProgress>(_latest); }
    }

    // ---- loop ------------------------------------------------------------------------------------------------------

    /// <summary>Runs until cancelled: one cycle, then sleep for the delta interval or until <see cref="RequestSync"/>.</summary>
    public async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(force: false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per-entity failures are already recorded; this catches anything else so the loop never dies.
                LastError = ex;
                _logger.LogError(ex, "Sync cycle failed");
            }

            var wake = _wake = NewWake();
            await Task.WhenAny(wake.Task, Task.Delay(_options.DeltaInterval, _time, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Wakes the loop without waiting for the cycle.</summary>
    public void RequestSync() => _wake.TrySetResult();

    public Task SyncNowAsync(CancellationToken ct) => RunCycleAsync(force: true, ct);

    /// <summary>
    /// One full pass: delta entities in order, then the refresh entities when <paramref name="force"/> or their interval has elapsed.
    /// Only one cycle runs at a time; a second caller waits its turn.
    /// </summary>
    public async Task RunCycleAsync(bool force, CancellationToken ct)
    {
        await _cycleLock.WaitAsync(ct).ConfigureAwait(false);
        IsRunning = true;
        try
        {
            foreach (var entity in DeltaEntities)
            {
                await Guard(entity, () => SyncEntityAsync(entity, ct), ct).ConfigureAwait(false);
            }

            if (force || IsRefreshDue(CacheEntity.ManagedDevices))
            {
                await Guard(CacheEntity.ManagedDevices, () => RefreshManagedDevicesAsync(ct), ct).ConfigureAwait(false);
                await Guard(CacheEntity.Skus, () => RefreshSkusAsync(ct), ct).ConfigureAwait(false);
                await Guard(CacheEntity.Organization, () => RefreshOrganizationAsync(ct), ct).ConfigureAwait(false);
            }

            LastCycleCompleted = _time.GetUtcNow();
        }
        finally
        {
            IsRunning = false;
            _cycleLock.Release();
        }
    }

    // ---- delta entities --------------------------------------------------------------------------------------------

    /// <summary>Initial walk when no delta link is stored, otherwise a delta tick; a 410 clears the entity and re-walks.</summary>
    public async Task SyncEntityAsync(CacheEntity entity, CancellationToken ct)
    {
        var state = _db.GetSyncState(entity);
        if (state?.DeltaLink is null)
        {
            await WalkAsync(entity, SyncPhase.Initial, InitialUrl(entity), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await WalkAsync(entity, SyncPhase.Delta, state.DeltaLink, ct).ConfigureAwait(false);
        }
        catch (GraphException ex) when (IsResyncRequired(ex))
        {
            _logger.LogWarning("{Entity}: delta link no longer valid ({Code}); clearing and rebuilding", entity.Key(), ex.ErrorCode);
            _db.Clear(entity);
            await WalkAsync(entity, SyncPhase.Initial, InitialUrl(entity), ct).ConfigureAwait(false);
        }
    }

    private async Task WalkAsync(CacheEntity entity, SyncPhase phase, string url, CancellationToken ct)
    {
        int? total = phase == SyncPhase.Initial ? await TryCountAsync(entity, ct).ConfigureAwait(false) : null;
        var processed = 0;
        var touchedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Publish(entity, phase, processed, total, done: false);

        string? next = url;
        string? deltaLink = null;
        while (next is not null)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _graph.GetPageAsync(next, ct).ConfigureAwait(false);

            foreach (var chunk in page.Items.Chunk(Math.Max(1, _options.BatchSize)))
            {
                _db.Write(w =>
                {
                    foreach (var item in chunk)
                    {
                        Apply(w, entity, item, touchedGroups);
                    }

                    if (entity == CacheEntity.Groups && phase == SyncPhase.Delta)
                    {
                        foreach (var g in touchedGroups) w.RefreshMemberCount(g);
                        touchedGroups.Clear();
                    }
                });
                processed += chunk.Length;
                Publish(entity, phase, processed, total, done: false);
            }

            next = page.NextLink;
            deltaLink ??= page.DeltaLink;
        }

        var now = _time.GetUtcNow();
        _db.Write(w =>
        {
            if (entity == CacheEntity.Groups && phase == SyncPhase.Initial) w.RefreshMemberCount();
            var previous = w.GetSyncState(entity);
            w.SetSyncState(new SyncState
            {
                Entity = entity,
                DeltaLink = deltaLink ?? previous?.DeltaLink,
                LastFullSync = phase == SyncPhase.Initial ? now : previous?.LastFullSync,
                LastDeltaSync = phase == SyncPhase.Delta ? now : previous?.LastDeltaSync,
                RowCount = w.Count(entity),
            });
        });

        _logger.LogInformation("{Entity}: {Phase} applied {Processed} change(s)", entity.Key(), phase, processed);
        Publish(entity, phase, processed, total, done: true);
    }

    private static void Apply(CacheWriter w, CacheEntity entity, JsonElement item, HashSet<string> touchedGroups)
    {
        var id = item.GetString("id");
        if (id is null) return;

        if (item.IsRemoved())
        {
            w.Delete(entity, id);
            return;
        }

        switch (entity)
        {
            case CacheEntity.Users:
                w.UpsertUser(DirectoryUser.FromJson(item));
                break;
            case CacheEntity.Groups:
                w.UpsertGroup(DirectoryGroup.FromJson(item));
                ApplyMembersDelta(w, id, item, touchedGroups);
                break;
            case CacheEntity.Devices:
                w.UpsertDevice(EntraDevice.FromJson(item));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entity), entity, "not a delta entity");
        }
    }

    private static void ApplyMembersDelta(CacheWriter w, string groupId, JsonElement group, HashSet<string> touchedGroups)
    {
        if (!group.TryGetProperty("members@delta", out var members) || members.ValueKind != JsonValueKind.Array) return;
        foreach (var m in members.EnumerateArray())
        {
            var memberId = m.GetString("id");
            if (memberId is null) continue;
            if (m.IsRemoved())
            {
                w.RemoveGroupMember(groupId, memberId);
            }
            else
            {
                w.AddGroupMember(groupId, memberId, MemberType(m.GetString("@odata.type")));
            }
        }

        touchedGroups.Add(groupId);
    }

    /// <summary>"#microsoft.graph.user" → "user"; unknown types keep their last segment.</summary>
    public static string MemberType(string? odataType)
    {
        if (string.IsNullOrEmpty(odataType)) return "unknown";
        var dot = odataType.LastIndexOf('.');
        return dot >= 0 ? odataType[(dot + 1)..] : odataType;
    }

    // ---- refresh entities ------------------------------------------------------------------------------------------

    /// <summary>Full walk of Intune devices; rows not seen in this pass are deleted (retired/removed devices).</summary>
    public async Task RefreshManagedDevicesAsync(CancellationToken ct)
    {
        var url = $"deviceManagement/managedDevices?$select={GraphSelects.ManagedDevice}&$top={_options.ManagedDevicePageSize}";
        var items = new List<ManagedDevice>();
        Publish(CacheEntity.ManagedDevices, SyncPhase.Refresh, 0, null, done: false);
        await foreach (var item in _graph.GetAllPagesAsync(url, ct).ConfigureAwait(false))
        {
            items.Add(ManagedDevice.FromJson(item));
            if (items.Count % 250 == 0) Publish(CacheEntity.ManagedDevices, SyncPhase.Refresh, items.Count, null, done: false);
        }

        var now = _time.GetUtcNow();
        _db.Write(w =>
        {
            foreach (var d in items) w.UpsertManagedDevice(d);
            w.DeleteStale(CacheEntity.ManagedDevices);
            w.SetSyncState(new SyncState { Entity = CacheEntity.ManagedDevices, LastFullSync = now, RowCount = items.Count });
        });

        _logger.LogInformation("managed_devices: refreshed {Count} device(s)", items.Count);
        Publish(CacheEntity.ManagedDevices, SyncPhase.Refresh, items.Count, items.Count, done: true);
    }

    public async Task RefreshSkusAsync(CancellationToken ct)
    {
        var items = new List<LicenseSku>();
        await foreach (var item in _graph.GetAllPagesAsync($"subscribedSkus?$select={GraphSelects.SubscribedSku}", ct).ConfigureAwait(false))
        {
            items.Add(LicenseSku.FromJson(item));
        }

        var now = _time.GetUtcNow();
        _db.Write(w =>
        {
            w.DeleteAllSkus();
            foreach (var s in items) w.UpsertSku(s);
            w.SetSyncState(new SyncState { Entity = CacheEntity.Skus, LastFullSync = now, RowCount = items.Count });
        });

        Publish(CacheEntity.Skus, SyncPhase.Refresh, items.Count, items.Count, done: true);
    }

    public async Task RefreshOrganizationAsync(CancellationToken ct)
    {
        var page = await _graph.GetPageAsync($"organization?$select={GraphSelects.Organization}", ct).ConfigureAwait(false);
        var org = page.Items.Count > 0 ? Organization.FromJson(page.Items[0]) : null;
        if (org is null) return;

        var now = _time.GetUtcNow();
        _db.Write(w =>
        {
            w.UpsertOrganization(org);
            w.SetSyncState(new SyncState { Entity = CacheEntity.Organization, LastFullSync = now, RowCount = 1 });
        });

        Publish(CacheEntity.Organization, SyncPhase.Refresh, 1, 1, done: true);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private bool IsRefreshDue(CacheEntity entity)
    {
        var last = _db.GetSyncState(entity)?.LastFullSync;
        return last is null || _time.GetUtcNow() - last.Value >= _options.DeviceRefreshInterval;
    }

    private string InitialUrl(CacheEntity entity) => entity switch
    {
        CacheEntity.Users => $"users/delta?$select={GraphSelects.UserDelta}&$top={_options.DirectoryPageSize}",
        CacheEntity.Groups => $"groups/delta?$select={GraphSelects.Group},members&$top={_options.DirectoryPageSize}",
        CacheEntity.Devices => $"devices/delta?$select={GraphSelects.Device}&$top={_options.DirectoryPageSize}",
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, "not a delta entity"),
    };

    /// <summary><c>/users/$count</c> etc. for a progress denominator; failure just means no percentage.</summary>
    private async Task<int?> TryCountAsync(CacheEntity entity, CancellationToken ct)
    {
        var path = entity switch { CacheEntity.Users => "users/$count", CacheEntity.Groups => "groups/$count", CacheEntity.Devices => "devices/$count", _ => null };
        if (path is null) return null;
        try
        {
            using var doc = await _graph.SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
            return doc is not null && doc.RootElement.ValueKind == JsonValueKind.Number && doc.RootElement.TryGetInt32(out var n) ? n : null;
        }
        catch (GraphException ex)
        {
            _logger.LogDebug("{Entity}: $count unavailable ({Code})", entity.Key(), ex.ErrorCode);
            return null;
        }
    }

    private static bool IsResyncRequired(GraphException ex) =>
        ex.StatusCode == 410 ||
        string.Equals(ex.ErrorCode, "syncStateNotFound", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ex.ErrorCode, "resyncRequired", StringComparison.OrdinalIgnoreCase);

    private async Task Guard(CacheEntity entity, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex;
            _logger.LogError(ex, "{Entity}: sync failed", entity.Key());
            Publish(entity, SyncPhase.Delta, 0, null, done: true, error: ex);
        }
    }

    private void Publish(CacheEntity entity, SyncPhase phase, int processed, int? total, bool done, Exception? error = null)
    {
        var progress = new SyncProgress { Entity = entity, Phase = phase, Processed = processed, Total = total, IsDone = done, Error = error };
        lock (_latestLock) _latest[entity] = progress;
        ProgressChanged?.Invoke(this, progress);
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
