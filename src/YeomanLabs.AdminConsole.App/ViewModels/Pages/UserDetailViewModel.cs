using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// User 360 (UX §4). Renders from the cache instantly, then hydrates the live sections through one <c>$batch</c>;
/// each tab shows a skeleton until its section has loaded. F5 re-hydrates.
/// </summary>
public sealed partial class UserDetailViewModel : ObservableObject, IPageViewModel, IRefreshable, IDisposable
{
    private readonly UserService _users;
    private readonly TenantContext _tenant;
    private readonly ILogger<UserDetailViewModel> _logger;
    private CancellationTokenSource? _cts;

    public UserDetailViewModel(UserService users, TenantContext tenant, ILogger<UserDetailViewModel> logger)
    {
        _users = users;
        _tenant = tenant;
        _logger = logger;
    }

    public string Id { get; private set; } = string.Empty;

    public string Title => User?.DisplayName ?? User?.UserPrincipalName ?? "User";

    /// <summary>Cached projection first; replaced by the fresh copy once the batch returns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasUser))]
    private DirectoryUser? _user;

    public bool HasUser => User is not null;

    [ObservableProperty]
    private IReadOnlyList<ManagedDevice> _cachedDevices = [];

    [ObservableProperty]
    private IReadOnlyList<DirectoryGroup> _cachedGroups = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccessLoaded))]
    [NotifyPropertyChangedFor(nameof(IsActivityLoaded))]
    [NotifyPropertyChangedFor(nameof(IsDevicesLive))]
    private UserLiveDetail? _live;

    [ObservableProperty]
    private bool _isHydrating;

    [ObservableProperty]
    private string? _hydrateError;

    [ObservableProperty]
    private string _timingText = string.Empty;

    /// <summary>Identity renders from the cache, so it is "loaded" as soon as a user exists.</summary>
    public bool IsIdentityLoaded => User is not null;

    public bool IsAccessLoaded => Live is not null;
    public bool IsActivityLoaded => Live is not null;
    public bool IsDevicesLive => Live is not null;

    /// <summary>UX §4 tab 5 is Phase 3; disabled with the doc §6 reason.</summary>
    public string MailboxDisabledReason => "Exchange Online integration arrives in Phase 3";

    public bool HasP1 => _tenant.HasP1;

    /// <summary>Called once by the shell right after construction.</summary>
    public void Start(string id)
    {
        Id = id;
        _ = LoadAsync(CancellationToken.None);
    }

    public Task RefreshAsync(CancellationToken ct) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();

        // 1. Cache — must be instant (doc 01: < 500 ms fully populated from cache).
        var cached = await Task.Run(() => (_users.GetCached(Id), _users.GetCachedDevices(Id), _users.GetCachedGroups(Id)), cts.Token);
        if (cts.IsCancellationRequested) return;
        User ??= cached.Item1;
        if (cached.Item1 is not null) User = cached.Item1;
        CachedDevices = cached.Item2;
        CachedGroups = cached.Item3;
        var cacheMs = sw.Elapsed.TotalMilliseconds;

        // 2. Live — one $batch; the UI stays interactive with skeletons on the live tabs.
        IsHydrating = true;
        HydrateError = null;
        try
        {
            var live = await _users.HydrateAsync(Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            Live = live;
            if (live.User.Value is { } fresh) User = fresh;
            else if (live.User.Error is { } err) HydrateError = err.Message;
            TimingText = $"cache {cacheMs:F0} ms · live {live.ElapsedMilliseconds:F0} ms";
            _logger.LogDebug("User 360 {Id}: cache {CacheMs:F0} ms, live {LiveMs:F0} ms", Id, cacheMs, live.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // page closed or refreshed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "User 360 {Id}: hydration failed", Id);
            HydrateError = ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsHydrating = false;
        }
    }

    public void Dispose() => _cts?.Cancel();
}
