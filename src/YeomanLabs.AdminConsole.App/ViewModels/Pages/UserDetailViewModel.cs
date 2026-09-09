using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// User 360 (UX §4). Renders from the cache instantly, then hydrates the live sections through one <c>$batch</c>;
/// each tab shows a skeleton until its section has loaded. F5 re-hydrates. The Activity tab's audit list loads lazily
/// the first time the tab is selected (doc 04: 2–5 s on big tenants).
/// </summary>
public sealed partial class UserDetailViewModel : ObservableObject, IPageViewModel, IRefreshable, IDisposable
{
    private const int ActivityTabIndex = 3;
    private const string HybridCaption = "Mastered in on-prem AD — edit in AD until Phase 2";

    private readonly UserService _users;
    private readonly CacheDb _db;
    private readonly TenantContext _tenant;
    private readonly PermissionChecker _permissions;
    private readonly ToastService _toasts;
    private readonly ILogger<UserDetailViewModel> _logger;
    private CancellationTokenSource? _cts;
    private bool _auditRequested;

    public UserDetailViewModel(UserService users, CacheDb db, TenantContext tenant, PermissionChecker permissions, ToastService toasts, ILogger<UserDetailViewModel> logger)
    {
        _users = users;
        _db = db;
        _tenant = tenant;
        _permissions = permissions;
        _tenant.Changed += (_, _) => RecomputeReasons();
        _toasts = toasts;
        _logger = logger;
    }

    public string Id { get; private set; } = string.Empty;

    public string Title => User?.DisplayName ?? User?.UserPrincipalName ?? "User";

    /// <summary>Set by the shell: opens another object (device card click) in the current tab.</summary>
    public Action<ObjectType, string, string>? OpenObject { get; set; }

    // ---- state ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasUser))]
    [NotifyPropertyChangedFor(nameof(Initials))]
    [NotifyPropertyChangedFor(nameof(IsSynced))]
    private DirectoryUser? _user;

    public bool HasUser => User is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccessLoaded))]
    [NotifyPropertyChangedFor(nameof(IsActivityLoaded))]
    private UserLiveDetail? _live;

    [ObservableProperty]
    private bool _isHydrating;

    [ObservableProperty]
    private string? _hydrateError;

    [ObservableProperty]
    private string _timingText = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex;

    public bool IsAccessLoaded => Live is not null;
    public bool IsActivityLoaded => Live is not null;

    public string MailboxDisabledReason => "Exchange Online integration arrives in Phase 3";

    /// <summary>UX §6: consent first, then "not built yet". Recomputed whenever the granted scopes change.</summary>
    [ObservableProperty]
    private string? _actionsDisabledReason;

    [ObservableProperty]
    private string? _identityEditReason;

    [ObservableProperty]
    private string? _licenseEditReason;

    [ObservableProperty]
    private string? _groupEditReason;

    [ObservableProperty]
    private string? _authDeleteReason;

    private void RecomputeReasons()
    {
        ActionsDisabledReason = GreyRule.Reason(_permissions, [Capabilities.ResetPassword, Capabilities.RevokeSessions], "Reset password and Revoke sessions arrive in M5; Offboard in M6");
        IdentityEditReason = GreyRule.Reason(_permissions, Capabilities.EditUser, "Editing fields arrives in M5.2");
        LicenseEditReason = GreyRule.Reason(_permissions, Capabilities.AssignLicense, "License toggles arrive in M5.3");
        GroupEditReason = GreyRule.Reason(_permissions, Capabilities.ChangeGroupMembership, "Adding and removing groups arrives in M5.4");
        AuthDeleteReason = GreyRule.Reason(_permissions, Capabilities.DeleteAuthMethod, "Deleting methods arrives in M5.5");
    }

    // ---- header ---------------------------------------------------------------------------------------------------

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

    // ---- sidebar --------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isSidebarOpen = true;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [ObservableProperty]
    private string _createdText = "—";

    [ObservableProperty]
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

    private static string Describe(GraphException? error) => error is null ? "—" : error.IsForbidden ? "No permission" : "Unavailable";

    // ---- Identity tab (UX §4 tab 1, hybrid rule per doc 05) --------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<IdentitySection> _identitySections = [];

    [ObservableProperty]
    private string? _identityCaption;

    private void RecomputeIdentity()
    {
        if (User is null) return;
        using var raw = User.ParseRaw();
        var e = raw.RootElement;
        var synced = User.OnPremisesSyncEnabled;
        IdentityCaption = synced ? "This account is synced from on-prem AD. Greyed fields are mastered there." : null;

        IdentityField F(string label, string prop, string? value, bool mono = false)
        {
            var ro = User.IsReadOnly(prop);
            return new IdentityField(label, prop, value, mono, ro, ro ? HybridCaption : null);
        }

        string? Join(string prop) { var v = e.GetStrings(prop); return v.Count == 0 ? null : string.Join(", ", v); }

        IdentitySections =
        [
            new IdentitySection("Name",
            [
                F("Display name", "displayName", e.GetString("displayName")),
                F("First name", "givenName", e.GetString("givenName")),
                F("Last name", "surname", e.GetString("surname")),
                F("User principal name", "userPrincipalName", e.GetString("userPrincipalName"), mono: true),
                F("Mail nickname", "mailNickname", e.GetString("mailNickname"), mono: true),
            ]),
            new IdentitySection("Job",
            [
                F("Job title", "jobTitle", e.GetString("jobTitle")),
                F("Department", "department", e.GetString("department")),
                F("Company", "companyName", e.GetString("companyName")),
                F("Office", "officeLocation", e.GetString("officeLocation")),
                F("Employee ID", "employeeId", e.GetString("employeeId"), mono: true),
                F("Employee type", "employeeType", e.GetString("employeeType")),
                F("Hire date", "employeeHireDate", TimeText.Absolute(e.GetDate("employeeHireDate")) is var hd && hd != "—" ? hd : null),
            ]),
            new IdentitySection("Contact",
            [
                F("Email", "mail", e.GetString("mail"), mono: true),
                F("Other emails", "otherMails", Join("otherMails"), mono: true),
                F("Proxy addresses", "proxyAddresses", Join("proxyAddresses"), mono: true),
                F("Business phones", "businessPhones", Join("businessPhones"), mono: true),
                F("Mobile phone", "mobilePhone", e.GetString("mobilePhone"), mono: true),
            ]),
            new IdentitySection("Address",
            [
                F("Street", "streetAddress", e.GetString("streetAddress")),
                F("City", "city", e.GetString("city")),
                F("State", "state", e.GetString("state")),
                F("Postal code", "postalCode", e.GetString("postalCode")),
                F("Country", "country", e.GetString("country")),
            ]),
            new IdentitySection("Settings",
            [
                F("Usage location", "usageLocation", e.GetString("usageLocation")),
                F("Preferred language", "preferredLanguage", e.GetString("preferredLanguage")),
                F("User type", "userType", e.GetString("userType")),
                F("Account enabled", "accountEnabled", User.AccountEnabled ? "Yes" : "No"),
            ]),
        ];
    }

    // ---- Access tab (UX §4 tab 2) ----------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<LicenseRow> _licenses = [];

    [ObservableProperty]
    private IReadOnlyList<DirectoryGroup> _groups = [];

    [ObservableProperty]
    private string? _groupsCaption;

    [ObservableProperty]
    private IReadOnlyList<DirectoryRoleRef> _roles = [];

    [ObservableProperty]
    private IReadOnlyList<AuthMethod> _authMethods = [];

    [ObservableProperty]
    private string? _authMethodsError;

    [ObservableProperty]
    private string? _lastMfaRegistrationText;

    private void RecomputeAccess(IReadOnlyList<DirectoryGroup> cachedGroups)
    {
        // Licenses: every tenant SKU, checked when assigned; states come from the live batch, assignment from the user.
        var skus = _db.GetSkus();
        var states = Live?.LicenseStates.Value?.ToDictionary(s => s.SkuId, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, LicenseAssignmentState>();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (User is not null)
        {
            using var raw = User.ParseRaw();
            if (raw.RootElement.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in al.EnumerateArray()) if (l.GetString("skuId") is { } sid) assigned.Add(sid);
            }
        }

        Licenses = skus
            .Where(s => s.IsEnabled || assigned.Contains(s.SkuId))
            .Select(s => new LicenseRow(s, assigned.Contains(s.SkuId), states.GetValueOrDefault(s.SkuId)))
            .OrderByDescending(r => r.IsAssigned).ThenBy(r => r.Name)
            .ToList();

        // Groups: live list when available (authoritative), else the cache; member counts always from the cache.
        var counts = cachedGroups.ToDictionary(g => g.Id, g => g.MemberCount, StringComparer.OrdinalIgnoreCase);
        if (Live?.Groups.IsSuccess == true)
        {
            Groups = Live.Groups.Value!.Select(g => g with { MemberCount = counts.GetValueOrDefault(g.Id) }).OrderBy(g => g.DisplayName).ToList();
            GroupsCaption = null;
        }
        else
        {
            Groups = cachedGroups;
            GroupsCaption = Live?.Groups.Error is { } err ? $"Showing cached memberships — live read failed ({err.ErrorCode})" : "Showing cached memberships";
        }

        Roles = Live?.Roles.Value ?? [];

        if (Live?.AuthMethods.IsSuccess == true)
        {
            AuthMethods = Live.AuthMethods.Value!.OrderBy(m => m.Kind == AuthMethodKind.Password).ThenBy(m => m.KindLabel).ToList();
            AuthMethodsError = null;
            var last = AuthMethods.Where(m => m.Kind != AuthMethodKind.Password).Select(m => m.CreatedDateTime).Max();
            LastMfaRegistrationText = last is null ? null : $"Last MFA registration {TimeText.Both(last)}";
        }
        else if (Live?.AuthMethods.Error is { } e2)
        {
            AuthMethods = [];
            AuthMethodsError = e2.IsForbidden ? "Needs the 'UserAuthenticationMethod.Read.All' permission" : $"Could not read authentication methods ({e2.ErrorCode})";
        }
    }

    // ---- Devices tab (UX §4 tab 3) ---------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<DeviceCard> _deviceCards = [];

    [ObservableProperty]
    private IReadOnlyList<EntraDevice> _registeredOnlyDevices = [];

    [ObservableProperty]
    private string? _devicesCaption;

    [RelayCommand]
    private void OpenDevice(DeviceCard card) => OpenObject?.Invoke(ObjectType.Device, card.Id, card.Name);

    private async Task RefreshDeviceStatesAsync(CancellationToken ct)
    {
        if (DeviceCards.Count == 0) return;
        try
        {
            var states = await _users.GetDeviceStatesAsync(DeviceCards.Select(c => c.Id).ToList(), ct);
            foreach (var card in DeviceCards)
            {
                if (states.TryGetValue(card.Id, out var s)) card.ApplyLive(s);
            }
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Live device states for {UserId}: {Code}", Id, ex.ErrorCode);
            DevicesCaption = $"Showing cached device state — live read failed ({ex.ErrorCode})";
        }
    }

    private void RecomputeRegisteredOnly()
    {
        // Entra device objects registered to the user that Intune does not manage (BYOD registrations, old objects).
        if (Live?.RegisteredDevices.Value is not { } registered) return;
        var managedAad = new HashSet<string>(DeviceCards.Select(c => c.Device.AzureAdDeviceId ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        RegisteredOnlyDevices = registered.Where(d => !managedAad.Contains(d.DeviceId)).OrderBy(d => d.DisplayName).ToList();
    }

    // ---- Activity tab (UX §4 tab 4) --------------------------------------------------------------------------------

    public bool HasP1 => _tenant.HasP1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignInUnavailable))]
    private bool _signInActivityUnavailable;

    public bool ShowSignInUnavailable => SignInActivityUnavailable || !HasP1;

    [ObservableProperty]
    private string _lastInteractiveText = "—";

    [ObservableProperty]
    private string _lastNonInteractiveText = "—";

    [ObservableProperty]
    private string _lastSuccessfulText = "—";

    [ObservableProperty]
    private string _lastPasswordChangeText = "—";

    [ObservableProperty]
    private IReadOnlyList<AuditEvent> _auditEvents = [];

    [ObservableProperty]
    private bool _isAuditLoading;

    [ObservableProperty]
    private string? _auditCaption;

    private void RecomputeActivity()
    {
        if (User is null) return;
        using (var raw = User.ParseRaw())
        {
            LastPasswordChangeText = TimeText.Both(raw.RootElement.GetDate("lastPasswordChangeDateTime"));
        }

        if (Live is null) return;
        SignInActivityUnavailable = Live.SignInActivityUnavailable;
        var s = Live.SignInActivity;
        LastInteractiveText = TimeText.Both(s?.LastSignInDateTime);
        LastNonInteractiveText = TimeText.Both(s?.LastNonInteractiveSignInDateTime);
        LastSuccessfulText = TimeText.Both(s?.LastSuccessfulSignInDateTime);
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == ActivityTabIndex && !_auditRequested)
        {
            _auditRequested = true;
            _ = LoadAuditAsync(_cts?.Token ?? CancellationToken.None);
        }
    }

    private async Task LoadAuditAsync(CancellationToken ct)
    {
        IsAuditLoading = true;
        AuditCaption = null;
        try
        {
            var result = await _users.GetAuditEventsAsync(Id, ct);
            if (result.IsSuccess)
            {
                AuditEvents = result.Value!;
                if (AuditEvents.Count == 0) AuditCaption = "No audit events target this user in the retained window.";
            }
            else
            {
                AuditCaption = result.IsForbidden ? "Audit events need the 'AuditLog.Read.All' permission." : $"Could not load audit events ({result.Error!.ErrorCode}).";
            }
        }
        catch (OperationCanceledException)
        {
            // page closed
        }
        finally
        {
            IsAuditLoading = false;
        }
    }

    // ---- copy to clipboard with toast ---------------------------------------------------------------------------

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

    public void Start(string id)
    {
        Id = id;
        RecomputeReasons();
        _ = LoadAsync(CancellationToken.None);
    }

    public Task RefreshAsync(CancellationToken ct)
    {
        _auditRequested = false;
        return LoadAsync(ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();

        // 1. Cache — instant (doc 01: < 500 ms fully populated from cache).
        var cached = await Task.Run(() => (_users.GetCached(Id), _users.GetCachedDevices(Id), _users.GetCachedGroups(Id)), cts.Token);
        if (cts.IsCancellationRequested) return;
        var cachedGroups = cached.Item3;
        if (cached.Item1 is not null) User = cached.Item1;
        DeviceCards = cached.Item2.Select(d => new DeviceCard(d)).ToList();
        RecomputeBadges();
        RecomputeFacts();
        RecomputeIdentity();
        RecomputeAccess(cachedGroups);
        RecomputeActivity();
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
            RecomputeBadges();
            RecomputeFacts();
            RecomputeIdentity();
            RecomputeAccess(cachedGroups);
            RecomputeActivity();
            RecomputeRegisteredOnly();
            TimingText = $"cache {cacheMs:F0} ms · live {live.ElapsedMilliseconds:F0} ms";
            _logger.LogDebug("User 360 {Id}: cache {CacheMs:F0} ms, live {LiveMs:F0} ms", Id, cacheMs, live.ElapsedMilliseconds);

            if (SelectedTabIndex == ActivityTabIndex && !_auditRequested)
            {
                _auditRequested = true;
                _ = LoadAuditAsync(cts.Token);
            }

            // 3. Live device compliance (doc 01: live fields refreshed in background within 2 s).
            await RefreshDeviceStatesAsync(cts.Token);
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
