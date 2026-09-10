using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Writes;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One Active Directory object, and everything ADUC would let you do to it (M12.6, M12.11).
/// </summary>
/// <remarks>
/// <para>
/// For somebody who exists only in AD, this page is their <em>whole</em> page — there is no User 360 to fall back
/// to. So it carries what ADUC's tabs carry: every attribute editable in place, the account options, expiry, the
/// manager, group membership, and the password. A fact sheet with four buttons would send an admin back to ADUC for
/// the fifth thing, and then they keep ADUC open beside this for good.
/// </para>
/// <para>
/// One page for users, groups and computers rather than three, because in AD they are the same object with different
/// attributes: all three live in an OU, all three can be moved and renamed and deleted, and two of the three have a
/// <c>userAccountControl</c>. The differences — LAPS, group scope, machine-account reset — are easier to see against
/// a shared shape than as three separate pages that mostly repeat each other.
/// </para>
/// <para>
/// Every action a domain controller might refuse asks first where it can, and says what happened where it cannot.
/// Nothing here is greyed without a sentence beside it.
/// </para>
/// </remarks>
public sealed partial class DomainObjectViewModel : ObservableObject, IPageViewModel, IRefreshable
{
    private readonly IDirectoryService _directory;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;
    private readonly ILogger<DomainObjectViewModel> _logger;

    public DomainObjectViewModel(IDirectoryService directory, IDialogService dialogs, ToastService toasts,
        ActionLog actions, TenantContext tenant, ILogger<DomainObjectViewModel> logger)
    {
        _directory = directory;
        _dialogs = dialogs;
        _toasts = toasts;
        _actions = actions;
        _tenant = tenant;
        _logger = logger;
    }

    public Guid ObjectGuid { get; private set; }

    public OnPremKind Kind { get; private set; }

    public string Title => Name;

    /// <summary>Set by the shell: closes this page and goes back to the Domain list.</summary>
    public Action? GoBack { get; set; }

    // ---- what is being looked at ---------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name = "…";

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _location = string.Empty;

    [ObservableProperty]
    private string? _distinguishedName;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Set when the object could not be read at all — deleted, or the DC went away mid-session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresent))]
    private string? _missingReason;

    public bool IsPresent => MissingReason is null;

    /// <summary>The read-only lines: sign-in name, timestamps, and the account flags in words.</summary>
    [ObservableProperty]
    private IReadOnlyList<FactRow> _facts = [];

    /// <summary>
    /// The editable attribute sheet (M12.11), in the same rows the User 360 identity tab uses. Each row's property
    /// name is the LDAP attribute itself, because on this page there is no Graph name to translate from.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSections))]
    private IReadOnlyList<EditableSection> _sections = [];

    public bool HasSections => Sections.Count > 0;

    public bool IsUser => Kind == OnPremKind.User;

    public bool IsComputer => Kind == OnPremKind.Computer;

    /// <summary>Users and computers have a membership list; a group's own memberships are on its facts instead.</summary>
    public bool HasMembership => Kind != OnPremKind.Group;

    /// <summary>The primary group is not in memberOf, and it is a different one for a machine than for a person.</summary>
    public string NoMembershipText => Kind == OnPremKind.Computer
        ? "Not a member of any group beyond Domain Computers."
        : "Not a member of any group beyond Domain Users.";

    // ---- enable and disable --------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnableLabel))]
    private bool _isEnabled = true;

    /// <summary>Groups have no account to enable, so the control is absent rather than greyed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountPanel))]
    private bool _hasAccountState;

    /// <summary>
    /// Whether the account card has anything in it at all. A group has neither an account to disable nor a LAPS
    /// password, and an empty bordered box on the page reads as something that failed to load.
    /// </summary>
    public bool HasAccountPanel => HasAccountState || IsComputer;

    public string EnableLabel => IsEnabled ? "Disable account" : "Enable account";

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        var turningOff = IsEnabled;
        if (turningOff)
        {
            // Disabling a computer stops it authenticating, which is not obvious from the word "disable" and is the
            // sort of thing worth being told before rather than after.
            var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
            {
                Heading = $"Disable {Name}?",
                Message = Kind == OnPremKind.Computer
                    ? $"{Name} will stop being able to authenticate to the domain. Anyone signed in stays signed in until they sign out."
                    : $"{Name} will not be able to sign in anywhere the domain controls.",
                ConfirmLabel = "Disable",
                Destructive = true,
            });

            if (!confirmed) return;
        }

        await RunAsync(
            ct => _directory.SetEnabledAsync(ObjectGuid, !turningOff, ct),
            turningOff ? "onprem.disable" : "onprem.enable");
    }

    // ---- the account tab (M12.11) --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    [NotifyPropertyChangedFor(nameof(LockoutText))]
    private bool _isLockedOut;

    public string LockoutText => IsLockedOut ? "Locked out — too many wrong passwords" : "Not locked out";

    [RelayCommand(CanExecute = nameof(IsLockedOut))]
    private Task UnlockAsync() => RunAsync(ct => _directory.UnlockAsync(ObjectGuid, ct), "onprem.unlock");

    /// <summary>
    /// Reset the password (ADUC's right-click action). Same dialog as the cloud reset: generate one that is shown
    /// once after it succeeds, or type one. Nothing here reaches the log, a toast, or the cache.
    /// </summary>
    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        var dialog = new PasswordResetDialogViewModel(Name)
        {
            Message = $"The current password for {Name} stops working straight away in Active Directory. This does not " +
                      "touch any cloud account.",
        };
        if (!await _dialogs.ShowAsync(dialog)) return;

        var wasGenerated = dialog.IsGenerated;
        var forceChange = dialog.ForceChangeNextSignIn;
        var password = dialog.Password;

        var outcome = await _directory.SetPasswordAsync(ObjectGuid, password, forceChange, CancellationToken.None);
        if (!outcome.Success)
        {
            _toasts.Show(outcome.FriendlyError ?? outcome.Summary);
            Record("onprem.password.reset", outcome.Summary, outcome.FriendlyError, succeeded: false);
            return;
        }

        Record("onprem.password.reset", $"Reset the AD password for {Name}",
            forceChange ? "must change at next sign-in" : "no change required at next sign-in");

        if (wasGenerated)
        {
            await _dialogs.ShowAsync(new PasswordRevealDialogViewModel(Name, password, forceChange)
            {
                Message = "This is the only time it is shown. Nothing here is written to the cache or the activity log.",
            });
        }

        _toasts.Show(outcome.Summary);
        await RefreshAsync(CancellationToken.None);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordExpiryLabel))]
    private bool _passwordNeverExpires;

    public string PasswordExpiryLabel => PasswordNeverExpires ? "Let the password expire" : "Set password to never expire";

    [RelayCommand]
    private Task TogglePasswordNeverExpiresAsync() =>
        RunAsync(ct => _directory.SetAccountOptionsAsync(ObjectGuid, passwordNeverExpires: !PasswordNeverExpires, null, ct),
            "onprem.account.options");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MustChangeLabel))]
    private bool _mustChangePassword;

    public string MustChangeLabel => MustChangePassword ? "Clear the change-at-next-sign-in requirement" : "Require a password change at next sign-in";

    [RelayCommand]
    private Task ToggleMustChangeAsync() =>
        RunAsync(ct => _directory.SetAccountOptionsAsync(ObjectGuid, null, mustChangePasswordAtNextLogon: !MustChangePassword, ct),
            "onprem.account.options");

    /// <summary>When the account stops working, in words, or "Never".</summary>
    [ObservableProperty]
    private string _accountExpiresText = "Never";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearExpiryCommand))]
    private bool _hasExpiry;

    /// <summary>Typed as a date. Parsed as one; anything else disables the button rather than sending nonsense.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetExpiryCommand))]
    private string _newExpiryText = string.Empty;

    private bool CanSetExpiry => TryParseExpiry(NewExpiryText, out _);

    private static bool TryParseExpiry(string text, out DateTimeOffset when)
    {
        // The end of the given day, which is what ADUC's "End of" date picker means.
        if (DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ||
            DateTime.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out d))
        {
            var endOfDay = d.Date.AddDays(1);
            when = new DateTimeOffset(endOfDay, TimeZoneInfo.Local.GetUtcOffset(endOfDay));
            return true;
        }

        when = default;
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanSetExpiry))]
    private async Task SetExpiryAsync()
    {
        if (!TryParseExpiry(NewExpiryText, out var when)) return;
        await RunAsync(ct => _directory.SetAccountExpiryAsync(ObjectGuid, when, ct), "onprem.account.expiry");
        NewExpiryText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(HasExpiry))]
    private Task ClearExpiryAsync() =>
        RunAsync(ct => _directory.SetAccountExpiryAsync(ObjectGuid, null, ct), "onprem.account.expiry");

    // ---- manager (M12.11) ----------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _managerText = "None";

    /// <summary>Everyone in the domain, for the manager picker. Loaded once, on first use.</summary>
    [ObservableProperty]
    private IReadOnlyList<OnPremUser> _people = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetManagerCommand))]
    private OnPremUser? _managerTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearManagerCommand))]
    private bool _hasManager;

    private bool CanSetManager => ManagerTarget is not null && ManagerTarget.ObjectGuid != ObjectGuid;

    [RelayCommand(CanExecute = nameof(CanSetManager))]
    private async Task SetManagerAsync()
    {
        if (ManagerTarget is not { } target) return;
        await RunAsync(ct => _directory.SetManagerAsync(ObjectGuid, target.ObjectGuid, ct), "onprem.manager");
        ManagerTarget = null;
    }

    [RelayCommand(CanExecute = nameof(HasManager))]
    private Task ClearManagerAsync() =>
        RunAsync(ct => _directory.SetManagerAsync(ObjectGuid, null, ct), "onprem.manager");

    // ---- member of (M12.11) --------------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<AdMembershipRow> _memberOf = [];

    /// <summary>Every group in the domain, for the add picker. Loaded once, on first use.</summary>
    [ObservableProperty]
    private IReadOnlyList<OnPremGroup> _groups = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToGroupCommand))]
    private OnPremGroup? _groupTarget;

    private bool CanAddToGroup => GroupTarget is not null;

    [RelayCommand(CanExecute = nameof(CanAddToGroup))]
    private async Task AddToGroupAsync()
    {
        if (GroupTarget is not { } group) return;
        await RunAsync(ct => _directory.AddToGroupAsync(group.ObjectGuid, ObjectGuid, ct), "onprem.group.member.add");
        GroupTarget = null;
    }

    [RelayCommand]
    private Task RemoveFromGroupAsync(AdMembershipRow row) =>
        RunAsync(ct => _directory.RemoveFromGroupAsync(row.ObjectGuid, ObjectGuid, ct), "onprem.group.member.remove");

    // ---- move ----------------------------------------------------------------------------------------------------

    /// <summary>Every OU in the domain, for the move picker. Loaded with the page since the tree is small.</summary>
    [ObservableProperty]
    private IReadOnlyList<OuNode> _organisationalUnits = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private OuNode? _moveTarget;

    private bool CanMove => MoveTarget is not null && !string.Equals(MoveTarget.DistinguishedName, ParentDn, StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task MoveAsync()
    {
        if (MoveTarget is not { } target) return;

        // The GUID does not change on a move, so anything holding one — this page included — stays valid.
        await RunAsync(ct => _directory.MoveAsync(ObjectGuid, target.DistinguishedName, ct), "onprem.move");
        MoveTarget = null;
    }

    private string? ParentDn => DistinguishedName is null ? null : DistinguishedNames.Parent(DistinguishedName);

    // ---- rename --------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    private string _newName = string.Empty;

    private bool CanRename => !string.IsNullOrWhiteSpace(NewName) && NewName.Trim() != Name;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        var wanted = NewName.Trim();

        // Renaming changes the object's name, not its sign-in name. Saying so stops an admin thinking they have
        // changed a username and finding out later that they have not.
        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Rename {Name}?",
            Message = $"It becomes “{wanted}” in Active Directory.",
            Bullets = Kind == OnPremKind.Group
                ? ["The group's sign-in name (sAMAccountName) is not changed by this."]
                : ["The sign-in name (sAMAccountName) and the user principal name are not changed by this."],
            ConfirmLabel = "Rename",
        });

        if (!confirmed) return;
        await RunAsync(ct => _directory.RenameAsync(ObjectGuid, wanted, ct), "onprem.rename");
        NewName = string.Empty;
    }

    // ---- computers: LAPS and the machine account -----------------------------------------------------------------

    [ObservableProperty]
    private bool _isReadingLaps;

    [RelayCommand]
    private async Task RevealLapsAsync()
    {
        IsReadingLaps = true;
        try
        {
            var result = await _directory.GetLapsPasswordAsync(ObjectGuid, CancellationToken.None);
            if (!result.HasPassword)
            {
                // Not an error: a forest without LAPS, a machine that has not checked in, and a delegation not
                // granted are three different situations, and the reason says which.
                await _dialogs.ShowAsync(new ConfirmDialogViewModel
                {
                    Heading = $"No LAPS password for {Name}",
                    Message = result.Reason ?? "Active Directory holds no password for this machine.",
                    ConfirmLabel = "Close",
                });

                return;
            }

            var password = result.Password!;

            // Reading one is worth recording; the password itself never goes near the log or a toast.
            Record("onprem.laps.reveal", $"Revealed the LAPS password for {Name}",
                $"{password.Source} LAPS, expires {password.ExpiresAt?.ToString("u") ?? "never"}");

            await _dialogs.ShowAsync(new PasswordRevealDialogViewModel(
                password.AccountName is null ? Name : $"{password.AccountName} on {Name}",
                password.Password,
                forceChange: false)
            {
                Message = Caption(password),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LAPS read failed for {Guid}", ObjectGuid);
            _toasts.Show("Could not read the LAPS password.");
        }
        finally
        {
            IsReadingLaps = false;
        }
    }

    private static string Caption(LapsPassword password)
    {
        var source = password.Source == LapsSource.Windows ? "Windows LAPS" : "the original Microsoft LAPS";
        var expiry = password.ExpiresAt is { } when
            ? when <= DateTimeOffset.UtcNow
                ? $"It expired {TimeText.Relative(when)} and the machine has not rotated it, which usually means it has not checked in."
                : $"Active Directory will rotate it {TimeText.Relative(when)}."
            : "Active Directory holds no rotation date for it.";

        return $"Stored by {source}. {expiry} Nothing here is written to the cache or the activity log.";
    }

    /// <summary>
    /// ADUC's "Reset Account". The consequence is the whole reason for the confirm: the machine's secure channel
    /// stops working the moment this succeeds, and an admin who did not know that spends the afternoon on "the
    /// trust relationship between this workstation and the primary domain failed".
    /// </summary>
    [RelayCommand]
    private async Task ResetComputerAccountAsync()
    {
        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Reset the account for {Name}?",
            Message = "The machine password goes back to its initial value. The machine will not be able to authenticate " +
                      "to the domain until it is rejoined. This is the fix for a broken trust relationship, not something to " +
                      "do to a machine that is working.",
            ConfirmLabel = "Reset account",
            Destructive = true,
        });

        if (!confirmed) return;
        await RunAsync(ct => _directory.ResetComputerAccountAsync(ObjectGuid, ct), "onprem.computer.reset");
    }

    // ---- delete --------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task DeleteAsync()
    {
        // Deleting an AD object is not recoverable without the recycle bin, and the recycle bin is off by default.
        // So this is the one action behind a typed name (UX §2) rather than a plain confirm.
        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Delete {Name}?",
            Message = Kind == OnPremKind.Computer
                ? $"{Name} is removed from Active Directory. The machine will need rejoining to the domain to come back."
                : $"{Name} is removed from Active Directory. Unless the recycle bin is enabled on this forest, it cannot be undone.",
            Bullets = [Location],
            ConfirmLabel = "Delete",
            Destructive = true,
            RequiredTypedValue = Name,
        });

        if (!confirmed) return;

        var outcome = await _directory.DeleteAsync(ObjectGuid, CancellationToken.None);
        Report(outcome, "onprem.delete");
        if (outcome.Success) GoBack?.Invoke();
    }

    // ---- the attribute sheet -------------------------------------------------------------------------------------

    /// <summary>
    /// Edit-on-blur save for one attribute. A null or empty value clears it, which is a delete rather than a write of
    /// "" — the same trap Graph sprang with <c>department</c>, and the one AD springs for most attributes.
    /// </summary>
    private async Task<WriteOutcome> SaveAttributeAsync(string ldapName, object? value)
    {
        var text = value switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
            IReadOnlyList<string> { Count: > 0 } list => list[0].Trim(),
            _ => null,
        };

        var outcome = await _directory.SetAttributesAsync(ObjectGuid, new Dictionary<string, string?> { [ldapName] = text }, CancellationToken.None);
        if (!outcome.Success)
        {
            return new WriteOutcome { Success = false, Summary = outcome.Summary, FriendlyError = outcome.FriendlyError };
        }

        if (outcome.WasNoOp) return WriteOutcome.NoOp(outcome.Summary);

        var summary = text is null ? $"Cleared {ldapName} on {Name}" : $"Set {ldapName} to {text} on {Name}";
        Record("onprem.setAttribute", summary, $"{ldapName} on {DistinguishedName}");
        _toasts.Show(summary);
        return new WriteOutcome { Success = true, Summary = summary };
    }

    private EditableField Field(string label, string ldapName, string? value, bool mono = false, string? placeholder = null) =>
        new(label, ldapName, value, SaveAttributeAsync, isMono: mono, placeholder: placeholder);

    // ---- running one write ---------------------------------------------------------------------------------------

    /// <summary>Performs a write, says what happened, records it, and re-reads so the page shows what is now true.</summary>
    private async Task RunAsync(Func<CancellationToken, Task<DirectoryOutcome>> write, string action)
    {
        IsLoading = true;
        try
        {
            Report(await write(CancellationToken.None), action);
            await RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Action} failed on {Guid}", action, ObjectGuid);
            _toasts.Show("The domain controller could not be reached.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Report(DirectoryOutcome outcome, string action)
    {
        if (!outcome.Success)
        {
            _toasts.Show(outcome.FriendlyError ?? outcome.Summary);
            Record(action, outcome.Summary, outcome.FriendlyError, succeeded: false);
            return;
        }

        // A warning means it worked and did something worth saying — a nesting loop, which AD allows. It is shown
        // instead of the plain summary, because the summary alone would be the less true of the two.
        _toasts.Show(outcome.Warning is null ? outcome.Summary : $"{outcome.Summary} — {outcome.Warning}");
        if (!outcome.WasNoOp) Record(action, outcome.Summary, outcome.Warning);
    }

    private void Record(string action, string summary, string? detail = null, bool succeeded = true) =>
        _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = Kind == OnPremKind.Computer ? ObjectType.Device : Kind == OnPremKind.Group ? ObjectType.Group : ObjectType.User,
            TargetId = ObjectGuid.ToString(),
            TargetName = Name,
            Action = action,
            RequestSummary = DistinguishedName,
            Outcome = succeeded ? ActionOutcome.Success : ActionOutcome.Failed,
            Message = detail is null ? summary : $"{summary} — {detail}",
        });

    // ---- loading -------------------------------------------------------------------------------------------------

    public void Start(Guid objectGuid, OnPremKind kind)
    {
        ObjectGuid = objectGuid;
        Kind = kind;
        OnPropertyChanged(nameof(IsComputer));
        OnPropertyChanged(nameof(IsUser));
        OnPropertyChanged(nameof(HasMembership));
        OnPropertyChanged(nameof(NoMembershipText));
        OnPropertyChanged(nameof(HasAccountPanel));
        _ = RefreshAsync(CancellationToken.None);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            if (OrganisationalUnits.Count == 0)
            {
                var ous = await _directory.GetOrganisationalUnitsAsync(ct);
                OrganisationalUnits = OuNode.Build(ous).SelectMany(r => r.Flatten()).ToList();
            }

            switch (Kind)
            {
                case OnPremKind.Group:
                    await LoadGroupAsync(ct);
                    break;
                case OnPremKind.Computer:
                    await LoadComputerAsync(ct);
                    break;
                default:
                    await LoadUserAsync(ct);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Page closed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read {Guid}", ObjectGuid);
            MissingReason = "The domain controller could not be reached.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// The group list, for membership. Loaded once: it doubles as the DN-to-GUID map the remove button needs, since
    /// <c>memberOf</c> holds distinguished names and the contract addresses groups by GUID. Capped at 500, which is
    /// plenty for a lab and worth revisiting for a large domain — a row past the cap keeps its name but loses its
    /// remove button, and says so.
    /// </summary>
    private async Task EnsureGroupsAsync(CancellationToken ct)
    {
        if (Groups.Count > 0) return;
        Groups = (await _directory.SearchGroupsAsync(string.Empty, 500, null, ct))
            .OrderBy(g => g.Primary, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task EnsurePeopleAsync(CancellationToken ct)
    {
        if (People.Count > 0) return;
        People = (await _directory.SearchUsersAsync(string.Empty, 500, null, ct))
            .Where(u => u.ObjectGuid != ObjectGuid)
            .OrderBy(u => u.Primary, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void LoadMembership(IReadOnlyList<string> memberOfDns)
    {
        var byDn = Groups.ToDictionary(g => g.DistinguishedName, g => g, StringComparer.OrdinalIgnoreCase);
        MemberOf = memberOfDns
            .Select(dn => byDn.TryGetValue(dn, out var g)
                ? new AdMembershipRow(g.Primary, g.ObjectGuid, dn, g.ScopeText)
                : new AdMembershipRow(DistinguishedNames.LeafName(dn) ?? dn, Guid.Empty, dn, "Beyond the first 500 groups loaded — remove it from the group's own page"))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task LoadUserAsync(CancellationToken ct)
    {
        var user = await _directory.GetUserAsync(ObjectGuid, ct);
        if (user is null) { NotFound("user"); return; }

        await EnsureGroupsAsync(ct);
        await EnsurePeopleAsync(ct);

        MissingReason = null;
        Name = user.Primary;
        Subtitle = user.UserPrincipalName ?? user.SamAccountName;
        DistinguishedName = user.DistinguishedName;
        Location = DistinguishedNames.Friendly(user.DistinguishedName);
        HasAccountState = true;
        IsEnabled = user.Enabled;
        IsLockedOut = user.IsLockedOut;
        PasswordNeverExpires = user.PasswordNeverExpires;
        MustChangePassword = user.MustChangePasswordAtNextLogon;
        HasExpiry = user.AccountExpires is not null;
        AccountExpiresText = user.AccountExpires is { } when
            ? (user.AccountHasExpired ? $"Expired {TimeText.Both(when)}" : $"Expires {TimeText.Both(when)}")
            : "Never";

        HasManager = user.ManagerDn is not null;
        ManagerText = user.ManagerDn is null ? "None"
            : People.FirstOrDefault(p => string.Equals(p.DistinguishedName, user.ManagerDn, StringComparison.OrdinalIgnoreCase))?.Primary
              ?? DistinguishedNames.LeafName(user.ManagerDn) ?? user.ManagerDn;

        LoadMembership(user.MemberOfDns);

        // Rebuilding on every refresh would throw away half-typed text, so update in place once the rows exist.
        if (Sections.Count > 0)
        {
            var values = UserValues(user);
            foreach (var field in Sections.SelectMany(s => s.Fields))
            {
                if (!field.IsSaving && values.TryGetValue(field.PropertyName, out var v)) field.Reset(v);
            }
        }
        else
        {
            Sections =
            [
                new EditableSection("Name",
                [
                    Field("Display name", "displayName", user.DisplayName),
                    Field("First name", "givenName", user.GivenName),
                    Field("Last name", "sn", user.Surname),
                    Field("User principal name", "userPrincipalName", user.UserPrincipalName, mono: true),
                    Field("Description", "description", user.Description),
                ]),
                new EditableSection("Job",
                [
                    Field("Job title", "title", user.JobTitle),
                    Field("Department", "department", user.Department),
                    Field("Company", "company", user.Company),
                    Field("Office", "physicalDeliveryOfficeName", user.Office),
                    Field("Employee ID", "employeeID", user.EmployeeId, mono: true),
                ]),
                new EditableSection("Contact",
                [
                    Field("Email", "mail", user.Mail, mono: true),
                    Field("Telephone", "telephoneNumber", user.TelephoneNumber, mono: true),
                    Field("Mobile", "mobile", user.Mobile, mono: true),
                ]),
                new EditableSection("Address",
                [
                    Field("Street", "streetAddress", user.StreetAddress),
                    Field("City", "l", user.City),
                    Field("State or county", "st", user.State),
                    Field("Postal code", "postalCode", user.PostalCode),
                    Field("Country", "co", user.Country, placeholder: "the name, e.g. United Kingdom"),
                ]),
            ];
        }

        var flags = string.Join(", ", AccountFlags.Describe(user.UserAccountControl));
        Facts =
        [
            new FactRow("Sign-in name", user.SamAccountName),
            new FactRow("Account", flags.Length > 0 ? flags : "Normal"),
            new FactRow("Created", TimeText.Both(user.WhenCreated)),
            new FactRow("Password last set", user.PasswordLastSet is null ? "Never" : TimeText.Both(user.PasswordLastSet)),
            new FactRow("Last logon", user.LastLogon is null ? "Never" : TimeText.Both(user.LastLogon)),
        ];
    }

    private static Dictionary<string, string?> UserValues(OnPremUser u) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["displayName"] = u.DisplayName, ["givenName"] = u.GivenName, ["sn"] = u.Surname,
        ["userPrincipalName"] = u.UserPrincipalName, ["description"] = u.Description,
        ["title"] = u.JobTitle, ["department"] = u.Department, ["company"] = u.Company,
        ["physicalDeliveryOfficeName"] = u.Office, ["employeeID"] = u.EmployeeId,
        ["mail"] = u.Mail, ["telephoneNumber"] = u.TelephoneNumber, ["mobile"] = u.Mobile,
        ["streetAddress"] = u.StreetAddress, ["l"] = u.City, ["st"] = u.State,
        ["postalCode"] = u.PostalCode, ["co"] = u.Country,
    };

    private async Task LoadGroupAsync(CancellationToken ct)
    {
        var group = await _directory.GetGroupAsync(ObjectGuid, ct);
        if (group is null) { NotFound("group"); return; }

        MissingReason = null;
        Name = group.Primary;
        Subtitle = group.ScopeText;
        DistinguishedName = group.DistinguishedName;
        Location = DistinguishedNames.Friendly(group.DistinguishedName);
        HasAccountState = false;

        Members = await _directory.GetGroupMembersAsync(ObjectGuid, ct);
        Scope = group.Scope;
        Category = group.Category;
        _loadedScope = group.Scope;
        _loadedCategory = group.Category;
        IsGroup = true;
        ConversionBlockedReason = GroupRules.Protected(group);

        Facts =
        [
            new FactRow("Sign-in name", group.SamAccountName),
            new FactRow("Scope", group.ScopeText),
            new FactRow("Description", Or(group.Description)),
            new FactRow("Direct members", group.MemberCount.ToString()),
            new FactRow("Member of", $"{group.MemberOfDns.Count} group{(group.MemberOfDns.Count == 1 ? string.Empty : "s")}"),
        ];
    }

    private async Task LoadComputerAsync(CancellationToken ct)
    {
        var computer = await _directory.GetComputerAsync(ObjectGuid, ct);
        if (computer is null) { NotFound("computer"); return; }

        await EnsureGroupsAsync(ct);

        MissingReason = null;
        Name = computer.Name;
        Subtitle = computer.OperatingSystem ?? computer.DnsHostName ?? computer.SamAccountName;
        DistinguishedName = computer.DistinguishedName;
        Location = DistinguishedNames.Friendly(computer.DistinguishedName);
        HasAccountState = true;
        IsEnabled = computer.Enabled;

        // A computer's memberOf is not on the projection; the group list already holds every member DN.
        LoadMembership(Groups
            .Where(g => g.MemberDns.Contains(computer.DistinguishedName, StringComparer.OrdinalIgnoreCase))
            .Select(g => g.DistinguishedName)
            .ToList());

        if (Sections.Count > 0)
        {
            foreach (var field in Sections.SelectMany(s => s.Fields))
            {
                if (field.IsSaving) continue;
                field.Reset(field.PropertyName == "description" ? computer.Description : computer.Location);
            }
        }
        else
        {
            Sections =
            [
                new EditableSection("Details",
                [
                    Field("Description", "description", computer.Description),
                    Field("Location", "location", computer.Location, placeholder: "e.g. Leeds office, floor 2"),
                ]),
            ];
        }

        Facts =
        [
            new FactRow("Account name", computer.SamAccountName),
            new FactRow("DNS name", Or(computer.DnsHostName)),
            new FactRow("Operating system", Or(computer.OperatingSystem)),
            new FactRow("Version", Or(computer.OperatingSystemVersion)),
            new FactRow("Created", TimeText.Both(computer.WhenCreated)),
            new FactRow("Last logon", computer.LastLogon is null ? "Never" : TimeText.Both(computer.LastLogon)),
        ];
    }

    private void NotFound(string what) =>
        MissingReason = $"This {what} is no longer in Active Directory. It may have been deleted since the list was loaded.";

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    // ---- group scope and category (M12.5, made reachable here) ---------------------------------------------------

    [ObservableProperty]
    private bool _isGroup;

    [ObservableProperty]
    private IReadOnlyList<OnPremMember> _members = [];

    public IReadOnlyList<GroupScope> Scopes { get; } = [GroupScope.Global, GroupScope.DomainLocal, GroupScope.Universal];

    public IReadOnlyList<GroupCategory> Categories { get; } = [GroupCategory.Security, GroupCategory.Distribution];

    private GroupScope _loadedScope;
    private GroupCategory _loadedCategory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    private GroupScope _scope;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    private GroupCategory _category;

    /// <summary>Set for a built-in group, which AD will not convert. Shown beside the control rather than instead of it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    private string? _conversionBlockedReason;

    private bool CanConvert => IsGroup && ConversionBlockedReason is null
        && (Scope != _loadedScope || Category != _loadedCategory);

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAsync()
    {
        // Turning a security group into a distribution one takes away every permission held through it, without
        // failing and without AD saying anything. That is worth a confirm; the rest is not.
        if (GroupRules.CategoryWarning(_loadedCategory, Category, Members.Count) is { } warning)
        {
            var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
            {
                Heading = $"Make {Name} a distribution group?",
                Message = warning,
                ConfirmLabel = "Convert",
                Destructive = true,
            });

            if (!confirmed) { Category = _loadedCategory; return; }
        }

        await RunAsync(ct => _directory.SetGroupTypeAsync(ObjectGuid, Scope, Category, ct), "onprem.group.convert");
    }
}

/// <summary>One label-and-value line on the AD object page.</summary>
public sealed record FactRow(string Label, string Value);

/// <summary>One row of an AD object's group membership, carrying the GUID the remove button needs (M12.11).</summary>
public sealed record AdMembershipRow(string Name, Guid ObjectGuid, string DistinguishedName, string Detail)
{
    /// <summary>False when the group was beyond the list that was loaded, so there is no GUID to remove by.</summary>
    public bool CanRemove => ObjectGuid != Guid.Empty;
}
