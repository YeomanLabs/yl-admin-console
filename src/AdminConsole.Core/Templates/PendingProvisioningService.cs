using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Templates;

/// <summary>
/// Finishes hybrid creates (M17.3). Run after every delta sync: an account waiting for Entra Connect is looked for in the
/// cache by its source anchor; when it appears, its cloud half runs; when its mailbox appears, its mailbox half runs. No
/// sync button (deferred 2026-10-17) — this waits for the schedule, and says when the next cycle is due.
/// </summary>
public sealed class PendingProvisioningService
{
    /// <summary>Entra Connect's default cycle.</summary>
    public static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(30);

    /// <summary>After this long without a mailbox, the item still waits but says so, once.</summary>
    public static readonly TimeSpan MailboxPatience = TimeSpan.FromHours(2);

    private readonly PendingProvisioningStore _store;
    private readonly CacheDb _db;
    private readonly UserProvisioningService _provisioning;
    private readonly ExchangeService _exchange;
    private readonly GraphClient _graph;
    private readonly TenantContext _tenant;
    private readonly ActionLog _actions;
    private readonly ILogger<PendingProvisioningService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PendingProvisioningService(PendingProvisioningStore store, CacheDb db, UserProvisioningService provisioning, ExchangeService exchange,
        GraphClient graph, TenantContext tenant, ActionLog actions, ILogger<PendingProvisioningService> logger)
    {
        _store = store;
        _db = db;
        _provisioning = provisioning;
        _exchange = exchange;
        _graph = graph;
        _tenant = tenant;
        _actions = actions;
        _logger = logger;
    }

    /// <summary>Raised after anything in the list changes; may fire on any thread.</summary>
    public event EventHandler? Changed;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>When Entra Connect last finished, as Graph reports it; refreshed by <see cref="ProcessAsync"/>.</summary>
    public DateTimeOffset? LastSync { get; private set; }

    public DateTimeOffset? NextSyncDue => LastSync + SyncInterval;

    public IReadOnlyList<PendingProvisioning> Items => _tenant.IsLoaded ? _store.Load(_tenant.TenantId) : [];

    public IReadOnlyList<PendingProvisioning> Open => [.. Items.Where(i => i.IsOpen)];

    public void Enqueue(PendingProvisioning item)
    {
        _store.Save(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Abandon(string id)
    {
        if (Items.FirstOrDefault(i => i.Id == id) is not { IsOpen: true } item) return;
        _store.Save(item.With($"Abandoned by {_tenant.SignedInUpn}") with { State = PendingState.Abandoned, FinishedAt = Time.GetUtcNow() });
        Record(item, "provision.abandon", ActionOutcome.Success, "Abandoned; the AD account stays as it is");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves every open item as far as it can go now. Safe to call often; one run at a time.</summary>
    public async Task ProcessAsync(CancellationToken ct)
    {
        if (!_tenant.IsLoaded || Open.Count == 0) return;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            await RefreshLastSyncAsync(ct).ConfigureAwait(false);
            foreach (var item in Open)
            {
                ct.ThrowIfCancellationRequested();
                var next = item.State == PendingState.WaitingForSync
                    ? await TryFinishCloudAsync(item, ct).ConfigureAwait(false)
                    : item;
                if (next.State == PendingState.WaitingForMailbox) next = await TryFinishMailboxAsync(next, ct).ConfigureAwait(false);
                if (next != item)
                {
                    _store.Save(next);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshLastSyncAsync(CancellationToken ct)
    {
        try
        {
            var page = await _graph.GetPageAsync("organization?$select=onPremisesLastSyncDateTime", ct).ConfigureAwait(false);
            if (page.Items.FirstOrDefault() is { } org && DateTimeOffset.TryParse(org.GetString("onPremisesLastSyncDateTime"), out var last)) LastSync = last;
        }
        catch (GraphException ex)
        {
            _logger.LogInformation("Could not read the last Entra Connect run: {Code}", ex.ErrorCode);
        }
    }

    private async Task<PendingProvisioning> TryFinishCloudAsync(PendingProvisioning item, CancellationToken ct)
    {
        if (_db.FindUserByImmutableId(item.ImmutableId) is not { } user) return item;

        var now = Time.GetUtcNow();
        var next = item.With($"Entra Connect brought {user.UserPrincipalName} up after {Minutes(now - item.CreatedAt)}") with
        {
            CloudUserId = user.Id,
            CloudUserPrincipalName = user.UserPrincipalName,
            SyncedAt = now,
        };

        var steps = await _provisioning.FinishCloudAsync(user, item.UsageLocation, item.SkuIds, item.Groups, ct).ConfigureAwait(false);
        foreach (var step in steps.Where(s => s.State != StepState.Skipped)) next = next.With($"{step.Title}: {step.Message}", step.State == StepState.Failed);
        var licensed = steps.Any(s => s.Title == "Assign licences" && s.State == StepState.Success);
        Record(next, "provision.cloud", steps.Any(s => s.State == StepState.Failed) ? ActionOutcome.Failed : ActionOutcome.Success,
            string.Join("; ", steps.Select(s => $"{s.Title}: {s.Message}")));

        var hasMailboxHalf = item.Exchange is { } x && (x.Language is not null || x.TimeZone is not null || x.MailboxAccess.Count > 0);
        if (hasMailboxHalf && item.SkuIds.Count == 0) next = next.With("No licence in the template, so no mailbox: the mailbox settings don't apply");

        // No licence asked for means no mailbox to wait for; a licence asked for and refused is a problem, said in
        // TryFinishMailboxAsync rather than waited out.
        var wantsMailbox = hasMailboxHalf && item.SkuIds.Count > 0;
        return next with
        {
            Licensed = licensed,
            State = wantsMailbox ? PendingState.WaitingForMailbox : PendingState.Done,
            FinishedAt = wantsMailbox ? null : Time.GetUtcNow(),
        };
    }

    private async Task<PendingProvisioning> TryFinishMailboxAsync(PendingProvisioning item, CancellationToken ct)
    {
        if (!item.Licensed)
        {
            return item.With("The mailbox settings were not applied: without a licence there is no mailbox to apply them to", problem: true)
                with { State = PendingState.Done, FinishedAt = Time.GetUtcNow() };
        }

        if (!_exchange.IsAvailable || item.CloudUserId is not { } userId) return item;
        var mailbox = await _exchange.GetMailboxAsync(userId, ct).ConfigureAwait(false);
        if (!mailbox.IsSuccess)
        {
            if (!mailbox.IsNotFound) return item.Notes.LastOrDefault()?.Text.StartsWith("Exchange:", StringComparison.Ordinal) == true ? item : item.With($"Exchange: {mailbox.Message}", problem: true);
            var waited = Time.GetUtcNow() - (item.SyncedAt ?? item.CreatedAt);
            return waited > MailboxPatience && !item.Notes.Any(n => n.Text.StartsWith("Still no mailbox", StringComparison.Ordinal))
                ? item.With($"Still no mailbox after {Minutes(waited)}; check the licence includes Exchange Online")
                : item;
        }

        var m = new MailboxRef(userId, item.DisplayName);
        var exchange = item.Exchange!;
        var next = item;
        if (exchange.Language is { } language && exchange.TimeZone is { } zone)
        {
            var regional = await _exchange.SetRegionalAsync(m, language, zone, localizeFolders: true, ct).ConfigureAwait(false);
            next = next.With(regional.Success ? regional.Summary : $"Regional settings: {regional.FriendlyError}", !regional.Success);
        }

        var upn = item.CloudUserPrincipalName ?? userId;
        foreach (var access in exchange.MailboxAccess)
        {
            var target = new MailboxRef(access.MailboxId, access.Label ?? access.MailboxId);
            if (access.FullAccess)
            {
                var full = await _exchange.GrantFullAccessAsync(target, upn, autoMapping: true, ct).ConfigureAwait(false);
                next = next.With(full.Success ? full.Summary : $"Full Access on {target.Label}: {full.FriendlyError}", !full.Success);
            }

            if (access.SendAs)
            {
                var sendAs = await _exchange.GrantSendAsAsync(target, upn, ct).ConfigureAwait(false);
                next = next.With(sendAs.Success ? sendAs.Summary : $"Send As on {target.Label}: {sendAs.FriendlyError}", !sendAs.Success);
            }
        }

        Record(next, "provision.exchange", next.HasProblems ? ActionOutcome.Failed : ActionOutcome.Success, "Mailbox half applied");
        return next with { State = PendingState.Done, FinishedAt = Time.GetUtcNow() };
    }

    private void Record(PendingProvisioning item, string action, ActionOutcome outcome, string message) =>
        _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = ObjectType.User,
            TargetId = item.CloudUserId ?? item.AdGuid.ToString(),
            TargetName = item.DisplayName,
            Action = action,
            RequestSummary = $"pending {item.Id}",
            Outcome = outcome,
            Message = message,
            CorrelationId = $"hybrid-{item.Id}",
        });

    private static string Minutes(TimeSpan t) => t.TotalMinutes < 1 ? "under a minute" : t.TotalMinutes < 90 ? $"{t.TotalMinutes:F0} min" : $"{t.TotalHours:F1} h";
}
