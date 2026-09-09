using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// User 360 (UX §4). Renders from the cache instantly, then hydrates the live sections through one <c>$batch</c>;
/// each tab shows a skeleton until its section has loaded. F5 re-hydrates. Header badges and the quick-facts sidebar
/// recompute whenever the user or the live detail changes.
/// </summary>
public sealed partial class UserDetailViewModel : ObservableObject, IPageViewModel, IRefreshable, IDisposable
{
    private readonly UserService _users;
    private readonly TenantContext _tenant;
    private readonly ToastService _toasts;
    private readonly ILogger<UserDetailViewModel> _logger;
    private CancellationTokenSource? _cts;

    public UserDetailViewModel(UserService users, TenantContext tenant, ToastService toasts, ILogger<UserDetailViewModel> logger)
    {
        _users = users;
        _tenant = tenant;
        _toasts = toasts;
        _logger = logger;
    }

    public string Id { get; private set; } = string.Empty;

    public string Title => User?.DisplayName ?? User?.UserPrincipalName ?? "User";

    // ---- state ----------------------------------------------------------------------------------------------------

    /// <summary>Cached projection first; replaced by the fresh copy once the batch returns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasUser))]
    [NotifyPropertyChangedFor(nameof(Initials))]
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

    public bool IsIdentityLoaded => User is not null;
    public bool IsAccessLoaded => Live is not null;
    public bool IsActivityLoaded => Live is not null;
    public bool IsDevicesLive => Live is not null;

    /// <summary>UX §4 tab 5 is Phase 3; disabled with the doc §6 reason.</summary>
    public string MailboxDisabledReason => "Exchange Online integration arrives in Phase 3";

    /// <summary>UX §6 grey rule for the header actions until M5/M6 wire them.</summary>
    public string ActionsDisabledReason => "Reset password and Revoke sessions arrive in M5; Offboard in M6";

    public bool HasP1 => _tenant.HasP1;

    // ---- header (UX §4) -------------------------------------------------------------------------------------------

    public string Initials
    {
        get
        {
            var name = User?.DisplayName ?? User?.UserPrincipalName ?? "?";
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
            return name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
        }
    }

    [ObservableProperty]
    private IReadOnlyList<BadgeItem> _badges = [];

    private void RecomputeBadges()
    {
        if (User is null)
        {
            Badges = [];
            return;
        }

        var list = new List<BadgeItem>(5)
        {
            User.AccountEnabled ? new BadgeItem("Enabled", BadgeTone.Good) : new BadgeItem("Disabled", BadgeTone.Danger, "Sign-in is blocked"),
            User.OnPremisesSyncEnabled ? new BadgeItem("Synced", BadgeTone.Info, "Mastered in on-prem AD; synced by Entra Connect") : new BadgeItem("Cloud-only"),
            User.IsGuest ? new BadgeItem("Guest", BadgeTone.Warning, "External identity") : new BadgeItem("Member"),
            User.Licensed ? new BadgeItem("Licensed") : new BadgeItem("Unlicensed", BadgeTone.Warning),
        };

        if (Live?.AuthMethods.IsSuccess == true)
        {
            var registered = Live.AuthMethods.Value!.Any(m => m.Kind is not (AuthMethodKind.Password or AuthMethodKind.Unknown));
            list.Add(registered ? new BadgeItem("MFA registered", BadgeTone.Good) : new BadgeItem("No MFA", BadgeTone.Warning, "No strong authentication method is registered"));
        }
        else if (Live?.AuthMethods.IsForbidden == true)
        {
            list.Add(new BadgeItem("MFA unknown", BadgeTone.Neutral, "Needs the 'UserAuthenticationMethod.Read.All' permission"));
        }

        Badges = list;
    }

    // ---- sidebar quick facts (UX §4) -------------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isSidebarOpen = true;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [ObservableProperty]
    private string _createdText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSynced))]
    private string? _lastSyncText;

    [ObservableProperty]
    private string? _sourceAnchor;

    [ObservableProperty]
    private string? _onPremDn;

    public bool IsSynced => User?.OnPremisesSyncEnabled == true;

    [ObservableProperty]
    private string _managerText = "…";

    [ObservableProperty]
    private string _directReportsText = "…";

    private void RecomputeFacts()
    {
        if (User is null) return;
        CreatedText = TimeText.Both(User.CreatedDateTime);
        using (var raw = User.ParseRaw())
        {
            var root = raw.RootElement;
            LastSyncText = User.OnPremisesSyncEnabled ? TimeText.Both(root.GetDate("onPremisesLastSyncDateTime")) : null;
            SourceAnchor = root.GetString("onPremisesImmutableId");
            OnPremDn = root.GetString("onPremisesDistinguishedName");
        }

        if (Live is null) return;
        ManagerText = Live.Manager.IsSuccess ? (Live.Manager.Value?.Label ?? "None") : Describe(Live.Manager.Error);
        DirectReportsText = Live.DirectReports.IsSuccess ? Live.DirectReports.Value!.Count.ToString() : Describe(Live.DirectReports.Error);
    }

    private static string Describe(Core.Graph.GraphException? error) => error is null ? "—" : error.IsForbidden ? "No permission" : "Unavailable";

    // ---- copy to clipboard with toast (UX §4: mono fields click to copy) ------------------------------------------

    [RelayCommand]
    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _toasts.Show($"Copied {Shorten(text)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard unavailable");
            _toasts.Show("Clipboard is unavailable right now");
        }
    }

    private static string Shorten(string s) => s.Length <= 48 ? s : s[..45] + "…";

    // ---- loading --------------------------------------------------------------------------------------------------

    /// <summary>Called once by the shell right after construction.</summary>
    public void Start(string id)
    {
        Id = id;
        _ = LoadAsync(CancellationToken.None);
    }

    public Task RefreshAsync(CancellationToken ct) => LoadAsync(ct);

    partial void OnUserChanged(DirectoryUser? value)
    {
        RecomputeBadges();
        RecomputeFacts();
    }

    partial void OnLiveChanged(UserLiveDetail? value)
    {
        RecomputeBadges();
        RecomputeFacts();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();

        // 1. Cache — must be instant (doc 01: < 500 ms fully populated from cache).
        var cached = await Task.Run(() => (_users.GetCached(Id), _users.GetCachedDevices(Id), _users.GetCachedGroups(Id)), cts.Token);
        if (cts.IsCancellationRequested) return;
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
            if (live.User.Value is { } fresh) User = fresh;
            else if (live.User.Error is { } err) HydrateError = err.Message;
            Live = live;
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
