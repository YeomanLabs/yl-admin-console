using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Groups;
using AdminConsole.Core.Licensing;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Users;
using AdminConsole.Core.Writes;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// User 360 (UX §4). Renders from the cache instantly, then hydrates the live sections through one <c>$batch</c>;
/// each tab shows a skeleton until its section has loaded. F5 re-hydrates. The Activity tab's audit list loads lazily
/// the first time the tab is selected (doc 04: 2–5 s on big tenants).
/// </summary>
public sealed partial class UserDetailViewModel : ObservableObject, IPageViewModel, IRefreshable, IDisposable
{
    private const int ActivityTabIndex = 3;
    private const int MailboxTabIndex = 4;
    private const int OneDriveTabIndex = 5;

    private readonly UserService _users;
    private readonly IDirectoryService _directory;
    private readonly CacheDb _db;
    private readonly TenantContext _tenant;
    private readonly PermissionChecker _permissions;
    private readonly ConsentService _consent;
    private readonly LicenseService _licenseService;
    private readonly GroupService _groupService;
    private readonly ToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly OffboardingRunner _offboarding;
    private readonly OffboardingProfileStore _profiles;
    private readonly ActionLog _actions;
    private readonly ILogger<UserDetailViewModel> _logger;
    private CancellationTokenSource? _cts;
    private bool _auditRequested;

    public UserDetailViewModel(UserService users, IDirectoryService directory, CacheDb db, TenantContext tenant, PermissionChecker permissions, ConsentService consent, LicenseService licenses, GroupService groups, ToastService toasts, IDialogService dialogs, OffboardingRunner offboarding, OffboardingProfileStore profiles, ActionLog actions, MailboxTabViewModel mailbox, ILogger<UserDetailViewModel> logger,
        SnapshotStore snapshots, [FromKeyedServices(RestoreRunnerKey)] OffboardingRunner restore, OneDriveTabViewModel oneDrive)
    {
        OneDrive = oneDrive;
        OneDrive.PickPerson = (prompt, onPicked) => PickPerson?.Invoke(prompt, onPicked);
        OneDrive.ReportStatus = text => ReportStatus?.Invoke(text);
        _snapshots = snapshots;
        _restore = restore;
        Mailbox = mailbox;
        // The picker is wired onto this page by the shell after construction, so read it at the moment of the pick.
        Mailbox.PickPerson = (prompt, onPicked) => PickPerson?.Invoke(prompt, onPicked);
        _users = users;
        _directory = directory;
        _db = db;
        _tenant = tenant;
        _permissions = permissions;
        _consent = consent;
        _licenseService = licenses;
        _groupService = groups;
        _tenant.Changed += (_, _) => RecomputeReasons();
        _toasts = toasts;
        _dialogs = dialogs;
        _offboarding = offboarding;
        _profiles = profiles;
        _actions = actions;
        _logger = logger;
    }

    /// <summary>The DI key of the runner that holds the restore catalog (M15.4); the unkeyed runner is the offboarding one.</summary>
    public const string RestoreRunnerKey = "restore";

    private readonly SnapshotStore _snapshots;
    private readonly OffboardingRunner _restore;

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
    [NotifyPropertyChangedFor(nameof(CanRunActions))]
    [NotifyPropertyChangedFor(nameof(CanOffboard))]
    [NotifyPropertyChangedFor(nameof(CanIssueTap))]
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

    /// <summary>The Mailbox tab (M13.5). Its own view model, loaded the first time the tab is selected.</summary>
    public MailboxTabViewModel Mailbox { get; }

    /// <summary>The OneDrive tab (M16.2). Its own view model, loaded the first time the tab is selected.</summary>
    public OneDriveTabViewModel OneDrive { get; }

    /// <summary>Set by the shell: the status bar, for work that outlives the page (a OneDrive export).</summary>
    public Action<string>? ReportStatus { get; set; }

    /// <summary>UX §6: consent first, then "not built yet". Recomputed whenever the granted scopes change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunActions))]
    private string? _actionsDisabledReason;

    /// <summary>
    /// Enables Reset password and Revoke sessions in the header. A permission not yet consented does not disable them:
    /// the caption says "click to grant", and the click does — each action asks for consent first (EnsureAsync). They
    /// used to be disabled, so the caption invited a click on a button that could not be clicked.
    /// </summary>
    public bool CanRunActions => HasUser;

    /// <summary>M18.1: the same permission as removing a method, so the same caption; a click asks for it.</summary>
    public bool CanIssueTap => HasUser;

    [ObservableProperty]
    private string? _authDeleteReason;

    /// <summary>Null once a user is loaded: offboarding needs a target, not a permission the run itself asks for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOffboard))]
    private string? _offboardDisabledReason;

    public bool CanOffboard => HasUser && OffboardDisabledReason is null;

    private void RecomputeReasons()
    {
        ActionsDisabledReason = GreyRule.Reason(_permissions, [Capabilities.ResetPassword, Capabilities.RevokeSessions], null);
        OffboardDisabledReason = null;
        AuthDeleteReason = GreyRule.Reason(_permissions, Capabilities.DeleteAuthMethod, null);
        OnPropertyChanged(nameof(CanIssueTap));

        // The caption under the list carries the consent hint; the remove buttons stay live and ask on the click.
        foreach (var row in AuthMethods) row.BlockedReason = null;
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
    private IReadOnlyList<EditableSection> _identitySections = [];

    [ObservableProperty]
    private string? _identityCaption;

    private void RecomputeIdentity()
    {
        if (User is null) return;
        using var raw = User.ParseRaw();
        var e = raw.RootElement;
        // A cloud-only account has nothing to say here. A synced one gets its caption from ApplyOnPremState, which
        // knows whether the probe has run and what it found; setting it here as well would mean writing a sentence
        // that is only true before the probe finishes.
        if (!User.OnPremisesSyncEnabled) IdentityCaption = null;

        // Rebuilding on every refresh would throw away half-typed text, so update in place once the rows exist.
        if (IdentitySections.Count > 0)
        {
            foreach (var field in IdentitySections.SelectMany(x => x.Fields))
            {
                if (!field.IsSaving) field.Reset(ReadField(e, field.PropertyName, field.IsArray));
            }

            // Reapplied here too, and this is the part that was missing: returning early left the caption at
            // whatever was written above, so a sheet whose fields the probe had already unlocked went on claiming
            // they were read-only. Correct on first load, wrong on every refresh after it - which is the version an
            // admin actually reads, because hydration triggers one a second later.
            ApplyOnPremState();
            return;
        }

        EditableField F(string label, string prop, bool mono = false, bool array = false, string? placeholder = null)
        {
            var ro = User!.IsReadOnly(prop);
            return new EditableField(label, prop, ReadField(e, prop, array), SaveFieldAsync, mono, array, ro,
                ro ? HybridUnlock.Checking : null, placeholder);
        }

        IdentitySections =
        [
            new EditableSection("Name",
            [
                F("Display name", "displayName"),
                F("First name", "givenName"),
                F("Last name", "surname"),
                F("User principal name", "userPrincipalName", mono: true),
                F("Mail nickname", "mailNickname", mono: true),
            ]),
            new EditableSection("Job",
            [
                F("Job title", "jobTitle"),
                F("Department", "department"),
                F("Company", "companyName"),
                F("Office", "officeLocation"),
                F("Employee ID", "employeeId", mono: true),
                F("Employee type", "employeeType"),
            ]),
            new EditableSection("Contact",
            [
                F("Email", "mail", mono: true),
                F("Business phones", "businessPhones", mono: true, array: true, placeholder: "comma separated"),
                F("Mobile phone", "mobilePhone", mono: true),
            ]),
            new EditableSection("Address",
            [
                F("Street", "streetAddress"),
                F("City", "city"),
                F("State", "state"),
                F("Postal code", "postalCode"),
                F("Country", "country"),
            ]),
            new EditableSection("Settings",
            [
                F("Usage location", "usageLocation", placeholder: "two-letter country code, e.g. GB"),
                F("Preferred language", "preferredLanguage", placeholder: "e.g. en-GB"),
            ]),
        ];

        // Rows are built read-only and may unlock once the on-prem probe has run, so whatever it last concluded is
        // reapplied here rather than being lost every time the sheet is rebuilt.
        ApplyOnPremState();
    }

    // ---- the hybrid unlock (M12.4) ---------------------------------------------------------------------------------

    /// <summary>The AD object behind a synced user, once found. Null while unknown, unreachable, or cloud-only.</summary>
    private OnPremUser? _onPrem;

    private DirectoryStatus _domainStatus = DirectoryStatus.NotConfigured;

    /// <summary>What AD says this admin may write on this object. Null means it did not say, so nothing is assumed.</summary>
    private IReadOnlySet<string>? _writable;

    private bool _onPremProbed;

    /// <summary>
    /// Finds the AD object behind a synced user so its mastered attributes can be edited here instead of in ADUC.
    /// </summary>
    /// <remarks>
    /// The join is <c>onPremisesImmutableId</c>, which is the base64 of the AD <c>objectGUID</c> — the only identifier
    /// that survives a rename or a move on either side. Matching on UPN or display name would silently target the
    /// wrong object the first time someone married or a name was reused.
    ///
    /// Being off the corporate network is the normal state of a laptop, so failing to reach a DC is not an error: the
    /// fields stay read-only and say why, which is what they did before this existed.
    /// </remarks>
    private async Task ProbeOnPremAsync(CancellationToken ct)
    {
        if (User is null || !User.OnPremisesSyncEnabled) return;

        string? immutableId;
        using (var raw = User.ParseRaw())
        {
            immutableId = raw.RootElement.GetString("onPremisesImmutableId");
        }

        try
        {
            _domainStatus = await _directory.GetStatusAsync(ct);
            _onPrem = _domainStatus.IsConnected && !string.IsNullOrEmpty(immutableId)
                ? await _directory.GetUserByImmutableIdAsync(immutableId, ct)
                : null;

            _writable = _onPrem is null ? null : await _directory.GetWritableAttributesAsync(_onPrem.ObjectGuid, ct);

            if (_domainStatus.IsConnected && OrganisationalUnits.Count == 0)
            {
                var ous = await _directory.GetOrganisationalUnitsAsync(ct);
                OrganisationalUnits = OuNode.Build(ous).SelectMany(r => r.Flatten()).ToList();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "On-prem lookup for {UserId} failed", Id);
            _domainStatus = DirectoryStatus.Unreachable(ex.Message);
            _onPrem = null;
        }

        _onPremProbed = true;
        MoveBlockedReason = _onPrem is not null ? null
            : _domainStatus.IsConnected ? "This account's AD object was not found, so there is nothing to move."
            : _domainStatus.Reason ?? "No domain controller is reachable from here.";
        MoveToOuCommand.NotifyCanExecuteChanged();
        ApplyOnPremState();
    }

    // ---- moving the AD object (M12.10) -----------------------------------------------------------------------------

    /// <summary>Every OU in the domain, loaded with the probe so the picker is populated before it is opened.</summary>
    [ObservableProperty]
    private IReadOnlyList<OuNode> _organisationalUnits = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveToOuCommand))]
    private OuNode? _moveTarget;

    /// <summary>Why the object cannot be moved from here, or null when it can. Never blank beside a dead control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowMove))]
    private string? _moveBlockedReason;

    /// <summary>Hidden entirely for a cloud-only account: there is no AD object, so there is nothing to move.</summary>
    public bool CanShowMove => User?.OnPremisesSyncEnabled == true;

    private bool CanMoveToOu =>
        _onPrem is not null &&
        MoveTarget is not null &&
        !string.Equals(MoveTarget.DistinguishedName, DistinguishedNames.Parent(_onPrem.DistinguishedName), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves the AD object without leaving the page. The alternative was to send the admin to the Domain list to
    /// find the same person again, which is the sort of round trip that makes people keep ADUC open beside this.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveToOu))]
    private async Task MoveToOuAsync()
    {
        if (_onPrem is not { } user || MoveTarget is not { } target) return;

        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Move {UserLabel} to {target.Name}?",
            Message = $"The AD object moves from {DistinguishedNames.Friendly(user.DistinguishedName)} to " +
                      $"{DistinguishedNames.FriendlyPath(target.DistinguishedName)}.",
            Bullets =
            [
                "The objectGUID does not change, so the link to this cloud account survives the move.",
                "An OU usually carries policy, so what applies to this account may change.",
            ],
            ConfirmLabel = "Move",
        });

        if (!confirmed) return;

        var outcome = await _directory.MoveAsync(user.ObjectGuid, target.DistinguishedName, CancellationToken.None);
        if (!outcome.Success)
        {
            _toasts.Show(outcome.FriendlyError ?? outcome.Summary);
            return;
        }

        _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = ObjectType.User,
            TargetId = user.ObjectGuid.ToString(),
            TargetName = UserLabel,
            Action = "onprem.user.move",
            RequestSummary = $"{user.DistinguishedName} -> {target.DistinguishedName}",
            Outcome = ActionOutcome.Success,
            Message = outcome.Summary,
        });

        _toasts.Show(outcome.Summary);
        MoveTarget = null;

        // Re-probe so the sidebar's on-prem DN and the field states reflect where the object now lives.
        await ProbeOnPremAsync(CancellationToken.None);
    }

    /// <summary>Puts every mastered field into the state the probe found, and captions the sheet to match.</summary>
    private void ApplyOnPremState()
    {
        if (User is null || !User.OnPremisesSyncEnabled) return;

        var mastered = IdentitySections.SelectMany(s => s.Fields).Where(f => User.IsReadOnly(f.PropertyName)).ToList();
        if (mastered.Count == 0) return;

        IdentityCaption = HybridUnlock.ForSheet(_domainStatus, _onPrem, _onPremProbed);
        if (!_onPremProbed) return;

        foreach (var field in mastered)
        {
            var state = HybridUnlock.ForField(field.PropertyName, _domainStatus, _onPrem is not null, _writable);
            if (state.CanEdit) field.UnlockForOnPrem(state.Caption);
            else field.KeepLocked(state.Caption);
        }
    }

    /// <summary>
    /// Writes one mastered attribute to AD (M12.4). The value will not come back from Graph until Entra Connect has
    /// run, so the field is told to hold what was written and show the waiting caption rather than a done tick.
    /// </summary>
    private async Task<WriteOutcome> SaveToDirectoryAsync(EditableField field, object? value)
    {
        var translation = OnPremAttributeMap.Translate(field.PropertyName, value);
        if (!translation.CanWrite)
        {
            return new WriteOutcome { Success = false, Summary = "Not saved", FriendlyError = translation.Refusal };
        }

        var outcome = await _directory.SetAttributesAsync(
            _onPrem!.ObjectGuid,
            new Dictionary<string, string?> { [translation.LdapName!] = translation.Value },
            CancellationToken.None);

        if (!outcome.Success)
        {
            return new WriteOutcome { Success = false, Summary = outcome.Summary, FriendlyError = outcome.FriendlyError };
        }

        if (outcome.WasNoOp) return WriteOutcome.NoOp(outcome.Summary);

        var summary = translation.Value is null
            ? $"Cleared {field.Label.ToLowerInvariant()} in AD on {UserLabel}"
            : $"Set {field.Label.ToLowerInvariant()} to {translation.Value} in AD on {UserLabel}";

        var actionId = _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = ObjectType.User,
            // The AD object's GUID, not the Entra id: this row records a change to the on-prem object, and the GUID
            // is what still identifies it if the account is later resynced or the cloud object is rebuilt.
            TargetId = _onPrem.ObjectGuid.ToString(),
            TargetName = UserLabel,
            Action = "onprem.user.setAttribute",
            RequestSummary = $"LDAP modify {translation.LdapName} on {_onPrem.DistinguishedName} via {_domainStatus.DomainController}",
            Outcome = ActionOutcome.Success,
            Message = summary,
        });

        // Undo writes the previous value straight back to AD. It is the value this sheet last agreed with the
        // directory, which is the same basis the Graph path's undo uses.
        var previous = field.CommittedValue;
        field.MarkAwaitingSync("Saved in AD — the cloud copy updates at the next Entra Connect sync");
        return new WriteOutcome
        {
            Success = true,
            Summary = summary,
            ActionId = actionId,
            Undo = ct => UndoDirectoryAsync(field, previous, ct),
        };
    }

    private async Task<WriteOutcome> UndoDirectoryAsync(EditableField field, string previous, CancellationToken ct)
    {
        var translation = OnPremAttributeMap.Translate(field.PropertyName, previous.Length == 0 ? null : previous);
        var outcome = await _directory.SetAttributesAsync(
            _onPrem!.ObjectGuid,
            new Dictionary<string, string?> { [translation.LdapName!] = translation.Value },
            ct);

        if (!outcome.Success)
        {
            return new WriteOutcome { Success = false, Summary = outcome.Summary, FriendlyError = outcome.FriendlyError };
        }

        field.MarkAwaitingSync("Put back in AD — the cloud copy updates at the next Entra Connect sync", previous);
        return new WriteOutcome { Success = true, Summary = $"Put {field.Label.ToLowerInvariant()} back in AD" };
    }

    private static string? ReadField(JsonElement e, string prop, bool array)
    {
        if (array)
        {
            var values = e.GetStrings(prop);
            return values.Count == 0 ? null : string.Join(", ", values);
        }

        return e.GetString(prop);
    }

    /// <summary>
    /// Edit-on-blur save (UX §4 tab 1). Requests write consent on the first write of the session, PATCHes one property,
    /// and raises the undo toast (UX §10). Failures come back to the field as an inline message (UX §7).
    /// </summary>
    private async Task<WriteOutcome> SaveFieldAsync(string propertyName, object? value)
    {
        // A mastered attribute on a synced account belongs to AD, so it goes to AD. Sending it to Graph would either
        // be rejected or — worse — accepted and then overwritten at the next sync.
        if (FieldFor(propertyName) is { WritesToOnPrem: true } onPremField && _onPrem is not null)
        {
            return await SaveToDirectoryAsync(onPremField, value);
        }

        var permission = _permissions.Check(Capabilities.EditUser);
        if (permission.NeedsConsent)
        {
            try
            {
                permission = await _consent.EnsureAsync(Capabilities.EditUser, CancellationToken.None);
            }
            catch (SignInException ex)
            {
                _logger.LogInformation("Consent for user edits not completed: {Kind}", ex.Kind);
                return new WriteOutcome { Success = false, Summary = "Not saved", FriendlyError = ConsentFailure.Inline(ex) };
            }

            if (!permission.CanRun)
            {
                return new WriteOutcome { Success = false, Summary = "Not saved", FriendlyError = permission.Reason };
            }
        }

        var outcome = await _users.UpdateAsync(Id, propertyName, value, CancellationToken.None);
        if (outcome.WasNoOp) return outcome;

        if (outcome.Success)
        {
            RefreshFromCache();
            _toasts.Show(outcome.Summary, outcome.CanUndo ? "Undo" : null, outcome.CanUndo ? () => UndoAsync(outcome) : null);
        }

        return outcome;
    }

    private async Task UndoAsync(WriteOutcome outcome)
    {
        var back = await outcome.Undo!(CancellationToken.None);
        RefreshFromCache();
        _toasts.Show(back.Success ? back.Summary : back.FriendlyError ?? "Undo failed");
    }

    private EditableField? FieldFor(string propertyName) =>
        IdentitySections.SelectMany(s => s.Fields)
            .FirstOrDefault(f => string.Equals(f.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Re-reads the written-through cache row so the header, badges and fields all reflect the new value.</summary>
    private void RefreshFromCache()
    {
        if (_users.GetCached(Id) is { } fresh) User = fresh;
        HasSnapshots = _snapshots.List(_tenant.TenantId, Id).Count > 0;
    }

    // ---- Access tab (UX §4 tab 2) ----------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<LicenseRow> _licenses = [];

    [ObservableProperty]
    private IReadOnlyList<GroupMembershipRow> _groups = [];

    [ObservableProperty]
    private string? _groupsCaption;

    [ObservableProperty]
    private IReadOnlyList<DirectoryRoleRef> _roles = [];

    [ObservableProperty]
    private IReadOnlyList<AuthMethodRow> _authMethods = [];

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

        // Consent is asked for on the first toggle (ToggleLicenseAsync), so a missing grant does not lock the rows.
        Licenses = skus
            .Where(s => s.IsEnabled || assigned.Contains(s.SkuId))
            .Select(s => new LicenseRow(s, assigned.Contains(s.SkuId), states.GetValueOrDefault(s.SkuId), null, ToggleLicenseAsync))
            .OrderByDescending(r => r.IsAssigned).ThenBy(r => r.Name)
            .ToList();

        // Groups: live list when available (authoritative), else the cache; member counts always from the cache.
        var counts = cachedGroups.ToDictionary(g => g.Id, g => g.MemberCount, StringComparer.OrdinalIgnoreCase);
        string? groupBlocked = null; // consent is asked for on the first removal (EnsureAsync), not a reason to lock the rows
        if (Live?.Groups.IsSuccess == true)
        {
            Groups = Live.Groups.Value!
                .Select(g => new GroupMembershipRow(g with { MemberCount = counts.GetValueOrDefault(g.Id) }, groupBlocked, RemoveFromGroupAsync))
                .OrderBy(g => g.Label).ToList();
            GroupsCaption = null;
        }
        else
        {
            Groups = cachedGroups.Select(g => new GroupMembershipRow(g, groupBlocked, RemoveFromGroupAsync)).ToList();
            GroupsCaption = Live?.Groups.Error is { } err ? $"Showing cached memberships — live read failed ({err.ErrorCode})" : "Showing cached memberships";
        }

        Roles = Live?.Roles.Value ?? [];

        if (Live?.AuthMethods.IsSuccess == true)
        {
            AuthMethods = Live.AuthMethods.Value!
                .OrderBy(m => m.Kind == AuthMethodKind.Password).ThenBy(m => m.KindLabel)
                .Select(m => new AuthMethodRow(m, null, DeleteAuthMethodAsync))
                .ToList();
            AuthMethodsError = null;
            var last = Live.AuthMethods.Value!.Where(m => m.Kind != AuthMethodKind.Password).Select(m => m.CreatedDateTime).Max();
            LastMfaRegistrationText = last is null ? null : $"Last MFA registration {TimeText.Both(last)}";
        }
        else if (Live?.AuthMethods.Error is { } e2)
        {
            AuthMethods = [];
            AuthMethodsError = e2.IsForbidden ? "Needs the 'UserAuthenticationMethod.Read.All' permission" : $"Could not read authentication methods ({e2.ErrorCode})";
        }
    }

    /// <summary>
    /// Checkbox toggle on the Access tab (UX §4 tab 2): consent on first write, assign or remove, undo toast.
    /// Returning a failed outcome makes the row put its checkbox back.
    /// </summary>
    private async Task<WriteOutcome> ToggleLicenseAsync(LicenseRow row, bool assign)
    {
        var permission = _permissions.Check(Capabilities.AssignLicense);
        if (permission.NeedsConsent)
        {
            try
            {
                permission = await _consent.EnsureAsync(Capabilities.AssignLicense, CancellationToken.None);
            }
            catch (SignInException ex)
            {
                return new WriteOutcome { Success = false, Summary = "Not changed", FriendlyError = ConsentFailure.Inline(ex) };
            }

            if (!permission.CanRun) return new WriteOutcome { Success = false, Summary = "Not changed", FriendlyError = permission.Reason };
        }

        var outcome = assign
            ? await _licenseService.AssignAsync(Id, row.SkuId, CancellationToken.None)
            : await _licenseService.RemoveAsync(Id, row.SkuId, CancellationToken.None);

        if (outcome.Success)
        {
            RefreshFromCache();
            _toasts.Show(outcome.Summary, outcome.CanUndo ? "Undo" : null, outcome.CanUndo ? () => UndoLicenseAsync(outcome, row, assign) : null);
        }
        else
        {
            _toasts.Show(outcome.FriendlyError ?? outcome.Summary);
        }

        return outcome;
    }

    private async Task UndoLicenseAsync(WriteOutcome outcome, LicenseRow row, bool wasAssign)
    {
        var back = await outcome.Undo!(CancellationToken.None);
        if (back.Success)
        {
            row.Set(!wasAssign);
            RefreshFromCache();
        }

        _toasts.Show(back.Success ? back.Summary : back.FriendlyError ?? "Undo failed");
    }

    /// <summary>Remove-X on a group row (UX §4 tab 2): consent, DELETE the membership, undo toast.</summary>
    private async Task RemoveFromGroupAsync(GroupMembershipRow row)
    {
        if (!await EnsureAsync(Capabilities.ChangeGroupMembership)) return;

        var label = User?.DisplayName ?? User?.UserPrincipalName ?? Id;
        var outcome = await _groupService.RemoveMemberAsync(row.Id, Id, label, CancellationToken.None);
        if (outcome.Success)
        {
            Groups = Groups.Where(g => g.Id != row.Id).ToList();
            _toasts.Show(outcome.Summary, "Undo", () => UndoMembershipAsync(outcome));
        }
        else
        {
            _toasts.Show(outcome.FriendlyError ?? outcome.Summary);
        }
    }

    /// <summary>"Add to group…" — the shell opens a groups-only picker and calls back with the chosen group.</summary>
    [RelayCommand]
    private void AddToGroup() => PickGroup?.Invoke(async group =>
    {
        if (!await EnsureAsync(Capabilities.ChangeGroupMembership)) return;

        var label = User?.DisplayName ?? User?.UserPrincipalName ?? Id;
        var outcome = await _groupService.AddMemberAsync(group.Id, Id, label, CancellationToken.None);
        if (outcome.Success)
        {
            RecomputeAccess(_users.GetCachedGroups(Id));
            _toasts.Show(outcome.Summary, "Undo", () => UndoMembershipAsync(outcome));
        }
        else
        {
            _toasts.Show(outcome.FriendlyError ?? outcome.Summary);
        }
    });

    /// <summary>Set by the shell: opens the palette in groups-only mode and invokes the callback with the pick.</summary>
    public Action<Func<DirectoryGroup, Task>>? PickGroup { get; set; }

    private async Task UndoMembershipAsync(WriteOutcome outcome)
    {
        var back = await outcome.Undo!(CancellationToken.None);
        if (back.Success) RecomputeAccess(_users.GetCachedGroups(Id));
        _toasts.Show(back.Success ? back.Summary : back.FriendlyError ?? "Undo failed");
    }

    /// <summary>Consent on the first write of the session; false means the admin declined or lacks the scope.</summary>
    private async Task<bool> EnsureAsync(Capability capability)
    {
        var permission = _permissions.Check(capability);
        if (!permission.NeedsConsent) return true;
        try
        {
            permission = await _consent.EnsureAsync(capability, CancellationToken.None);
        }
        catch (SignInException ex)
        {
            await ConsentFailure.ShowAsync(_dialogs, _toasts, ex);
            return false;
        }

        if (!permission.CanRun) _toasts.Show(permission.Reason ?? "Not permitted.");
        return permission.CanRun;
    }

    // ---- Security actions (UX §4 header, doc 04) --------------------------------------------------------------------

    private string UserLabel => User?.DisplayName ?? User?.UserPrincipalName ?? Id;

    /// <summary>
    /// Delete-X on a registered method (UX §4 tab 2). Irreversible — the admin cannot re-register it for the user — so
    /// it confirms first and offers no undo. A full re-hydrate follows so the MFA badge matches the list.
    /// </summary>
    private async Task DeleteAuthMethodAsync(AuthMethodRow row)
    {
        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Remove {row.KindLabel}?",
            Message = $"{UserLabel} will no longer be able to sign in with this method. Only they can register it again.",
            Bullets = [row.Detail is null ? $"{row.KindLabel} · {row.DisplayName}" : $"{row.KindLabel} · {row.DisplayName} · {row.Detail}"],
            ConfirmLabel = "Remove method",
            Destructive = true,
        });

        if (!confirmed) return;
        if (!await EnsureAsync(Capabilities.DeleteAuthMethod)) return;

        var outcome = await _users.DeleteAuthMethodAsync(Id, row.Method, CancellationToken.None);
        _toasts.Show(outcome.Success ? outcome.Summary : outcome.FriendlyError ?? outcome.Summary);
        if (outcome.Success) await RefreshAsync(CancellationToken.None);
    }

    /// <summary>
    /// Reset MFA (header): read the methods live, preview what goes and what stays, then remove them in the order Entra
    /// accepts (AuthMethodReset), optionally sign them out everywhere and issue a pass to register with. Each removal is
    /// its own action-log row; the end says what happened to every one.
    /// </summary>
    [RelayCommand]
    private async Task ResetMfaAsync()
    {
        IReadOnlyList<AuthMethod> methods;
        try
        {
            methods = await _users.ReadAuthMethodsAsync(Id, CancellationToken.None);
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Reading methods of {UserId} for a reset failed: {Code}", Id, ex.ErrorCode);
            _toasts.Show(ex.IsForbidden ? "Needs the 'UserAuthenticationMethod.Read.All' permission to read the methods." : GraphErrorText.Friendly(ex));
            return;
        }

        var dialog = new ResetMfaDialogViewModel(UserLabel, methods);
        if (!await _dialogs.ShowAsync(dialog)) return;
        if (!await EnsureAsync(Capabilities.DeleteAuthMethod)) return;

        var lines = new List<string>();
        var removed = 0;
        foreach (var method in dialog.Selected)
        {
            var outcome = await _users.DeleteAuthMethodAsync(Id, method, CancellationToken.None);
            if (outcome.Success) removed++;
            lines.Add(outcome.Success ? $"Removed {method.KindLabel} · {method.DisplayName}" : $"Not removed: {method.KindLabel} · {method.DisplayName} — {outcome.FriendlyError}");
        }

        if (dialog.RevokeSessions && await EnsureAsync(Capabilities.RevokeSessions))
        {
            var revoke = await _users.RevokeSessionsAsync(Id, CancellationToken.None);
            lines.Add(revoke.Success ? "Signed out everywhere" : $"Not signed out: {revoke.FriendlyError ?? revoke.Summary}");
        }

        var failed = dialog.Selected.Count - removed;
        var heading = failed == 0
            ? $"Removed {removed} method{(removed == 1 ? string.Empty : "s")} from {UserLabel}"
            : $"Removed {removed} of {dialog.Selected.Count} methods from {UserLabel}";
        await _dialogs.ShowAsync(new NoticeDialogViewModel(heading, string.Join(Environment.NewLine, lines)));
        await RefreshAsync(CancellationToken.None);

        if (dialog.IssueTapAfter) await IssueTapAsync();
    }

    /// <summary>
    /// Issue a Temporary Access Pass (M18.1). The pass is in memory only between the POST and the show-once dialog
    /// closing; the authentication methods list re-reads so the new pass appears there, without its code.
    /// </summary>
    [RelayCommand]
    private async Task IssueTapAsync()
    {
        var dialog = new TapDialogViewModel(UserLabel);
        if (!await _dialogs.ShowAsync(dialog)) return;
        if (!await EnsureAsync(Capabilities.IssueTap)) return;

        var result = await _users.IssueTemporaryAccessPassAsync(Id, dialog.LifetimeMinutes, dialog.UsableOnce, dialog.Start, CancellationToken.None);
        if (!result.Outcome.Success || result.Pass is not { } pass)
        {
            _toasts.Show(result.Outcome.FriendlyError ?? result.Outcome.Summary);
            return;
        }

        await _dialogs.ShowAsync(new SecretRevealDialogViewModel($"Temporary Access Pass for {UserLabel}", [new SecretRow("Pass", pass, result.Caption)])
        {
            Message = "This is the only time it is shown. Nothing here is written to the cache or the activity log.",
        });
        _toasts.Show(result.Outcome.Summary);
        await RefreshAsync(CancellationToken.None);
    }

    /// <summary>
    /// Revoke sessions (UX §4 header). Irreversible and immediate. Revoking your own sessions ends this app's token
    /// too, so that case says so in the confirm rather than surprising the admin with a sign-in prompt.
    /// </summary>
    [RelayCommand]
    private async Task RevokeSessionsAsync()
    {
        var isSelf = string.Equals(User?.UserPrincipalName, _tenant.SignedInUpn, StringComparison.OrdinalIgnoreCase);
        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = "Revoke all sessions?",
            Message = $"Every refresh token and browser session for {UserLabel} stops working. They will have to sign in again everywhere.",
            Bullets = isSelf
                ? ["This is your own account — you will be signed out of this app as well."]
                : [],
            ConfirmLabel = "Revoke sessions",
            Destructive = true,
        });

        if (!confirmed) return;
        if (!await EnsureAsync(Capabilities.RevokeSessions)) return;

        var outcome = await _users.RevokeSessionsAsync(Id, CancellationToken.None);
        _toasts.Show(outcome.Success ? outcome.Summary : outcome.FriendlyError ?? outcome.Summary);
    }

    /// <summary>
    /// Reset password (UX §4 header). Generated passwords are shown once, after the reset has succeeded; a typed one
    /// the admin already knows. Neither reaches a log, a toast, or the cache.
    /// </summary>
    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        var dialog = new PasswordResetDialogViewModel(UserLabel);
        if (!await _dialogs.ShowAsync(dialog)) return;
        if (!await EnsureAsync(Capabilities.ResetPassword)) return;

        var wasGenerated = dialog.IsGenerated;
        var forceChange = dialog.ForceChangeNextSignIn;
        var result = await _users.ResetPasswordAsync(Id, dialog.Password, forceChange, CancellationToken.None);
        if (!result.Outcome.Success)
        {
            _toasts.Show(result.Outcome.FriendlyError ?? result.Outcome.Summary);
            return;
        }

        if (wasGenerated && result.Password is { } password)
        {
            await _dialogs.ShowAsync(new PasswordRevealDialogViewModel(UserLabel, password, forceChange)
            {
                Message = "This is the only time it is shown. Nothing here is written to the cache or the activity log.",
            });
        }

        _toasts.Show(result.Outcome.Summary);
    }

    /// <summary>
    /// Offboard (UX §4 header, UX §8). The dialog reads the user live rather than from the cache, because a stale
    /// account state would make the preview describe a change that is not the one about to happen.
    /// </summary>
    [RelayCommand]
    private async Task OffboardAsync()
    {
        if (User is null) return;

        DirectoryUser target;
        try
        {
            target = await _users.ReadFreshAsync(Id, CancellationToken.None) ?? User;
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Could not re-read {UserId} before offboarding: {Code}", Id, ex.ErrorCode);
            _toasts.Show(GraphErrorText.Friendly(ex));
            return;
        }

        var dialog = new OffboardDialogViewModel(target, _offboarding, _profiles, _permissions, _consent, _actions, _tenant, _logger)
        {
            PickDelegate = PickPerson,
            ReadManager = ct => _users.ReadManagerAsync(target.Id, ct),
        };

        await dialog.PrepareAsync();
        await _dialogs.ShowAsync(dialog);
        RefreshFromCache();
        await RefreshAsync(CancellationToken.None);
    }

    /// <summary>Set by the shell: opens the palette restricted to users and calls back with the pick.</summary>
    public Action<string, Action<PersonRef>>? PickPerson { get; set; }

    /// <summary>True when an offboarding left a snapshot of this account behind, so there is something to restore from.</summary>
    [ObservableProperty]
    private bool _hasSnapshots;

    /// <summary>
    /// Restore from a snapshot (M15.4): the offboard dialog in reverse, with the restore catalog. Newest snapshot first;
    /// the AD account goes back where the snapshot found it unless the admin picks another OU.
    /// </summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (User is null) return;
        var snapshots = _snapshots.List(_tenant.TenantId, Id);
        if (snapshots.Count == 0)
        {
            _toasts.Show("There is no snapshot of this account. Offboarding takes one before it changes anything.");
            return;
        }

        DirectoryUser target;
        try
        {
            target = await _users.ReadFreshAsync(Id, CancellationToken.None) ?? User;
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Could not re-read {UserId} before restoring: {Code}", Id, ex.ErrorCode);
            _toasts.Show(GraphErrorText.Friendly(ex));
            return;
        }

        var ous = new List<OuChoice> { new(null, "Where the snapshot found it") };
        if (snapshots.Any(s => s.Snapshot.OnPrem is not null))
        {
            try
            {
                var all = await _directory.GetOrganisationalUnitsAsync(CancellationToken.None);
                ous.AddRange(all.OrderBy(o => o.DistinguishedName.Length).ThenBy(o => o.DistinguishedName, StringComparer.OrdinalIgnoreCase)
                    .Select(o => new OuChoice(o.DistinguishedName, DistinguishedNames.FriendlyPath(o.DistinguishedName))));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unreachable AD leaves the default choice; the AD rows say why they cannot run.
                _logger.LogInformation("OU list unavailable for restore: {Message}", ex.Message);
            }
        }

        var dialog = new OffboardDialogViewModel(target, _restore, _profiles, _permissions, _consent, _actions, _tenant, _logger)
        {
            Snapshots = [.. snapshots.Select(s => new SnapshotChoice(s.Path, s.Snapshot))],
            OuChoices = ous,
        };
        dialog.ChooseRestoreTarget(dialog.Snapshots[0], ous[0]);

        await dialog.RefreshPreviewAsync();
        await _dialogs.ShowAsync(dialog);
        RefreshFromCache();
        await RefreshAsync(CancellationToken.None);
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
        if (value == MailboxTabIndex) Mailbox.EnsureLoaded();
        if (value == OneDriveTabIndex) OneDrive.EnsureLoaded();

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
        HasSnapshots = _snapshots.List(_tenant.TenantId, Id).Count > 0;
        Mailbox.Start(id);
        OneDrive.Start(id);
        RecomputeReasons();
        _ = LoadAsync(CancellationToken.None);
    }

    public Task RefreshAsync(CancellationToken ct)
    {
        _auditRequested = false;
        Mailbox.Reset();
        OneDrive.Reset();
        if (SelectedTabIndex == MailboxTabIndex) Mailbox.EnsureLoaded();
        if (SelectedTabIndex == OneDriveTabIndex) OneDrive.EnsureLoaded();
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

        // Whether AD is reachable is asked alongside the Graph batch, not before it: an unreachable DC times out, and
        // waiting on that would hold up the whole page for a question that only affects one tab.
        var probe = ProbeOnPremAsync(cts.Token);

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
            await probe;
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

    public void Dispose()
    {
        _cts?.Cancel();
        Mailbox.Dispose();
        OneDrive.Dispose();
    }
}
