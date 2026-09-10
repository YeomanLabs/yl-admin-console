using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One Active Directory object, and the things that can be done to it (M12.6).
/// </summary>
/// <remarks>
/// <para>
/// One page for users, groups and computers rather than three, because in AD they are the same object with different
/// attributes: all three live in an OU, all three can be moved and renamed and deleted, and two of the three have a
/// <c>userAccountControl</c>. Splitting them would triple the code to say the same thing three times, and would make
/// the differences — LAPS, group scope — harder to see rather than easier.
/// </para>
/// <para>
/// Every action that a domain controller might refuse asks first where it can, and says what happened where it
/// cannot. Nothing here is greyed without a sentence beside it.
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

    [ObservableProperty]
    private IReadOnlyList<FactRow> _facts = [];

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

    // ---- LAPS ----------------------------------------------------------------------------------------------------

    public bool IsComputer => Kind == OnPremKind.Computer;

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
                    LoadComputer(await _directory.GetComputerAsync(ObjectGuid, ct));
                    break;
                default:
                    LoadUser(await _directory.GetUserAsync(ObjectGuid, ct));
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

    private void LoadUser(OnPremUser? user)
    {
        if (user is null) { NotFound("user"); return; }

        MissingReason = null;
        Name = user.Primary;
        Subtitle = user.UserPrincipalName ?? user.SamAccountName;
        DistinguishedName = user.DistinguishedName;
        Location = DistinguishedNames.Friendly(user.DistinguishedName);
        HasAccountState = true;
        IsEnabled = user.Enabled;

        Facts =
        [
            new FactRow("Sign-in name", user.SamAccountName),
            new FactRow("Job title", Or(user.JobTitle)),
            new FactRow("Department", Or(user.Department)),
            new FactRow("Member of", $"{user.MemberOfDns.Count} group{(user.MemberOfDns.Count == 1 ? string.Empty : "s")}"),
            new FactRow("Account", string.Join(", ", AccountFlags.Describe(user.UserAccountControl))),
            new FactRow("Created", TimeText.Both(user.WhenCreated)),
            new FactRow("Password last set", TimeText.Both(user.PasswordLastSet)),
            new FactRow("Last logon", user.LastLogon is null ? "Never" : TimeText.Both(user.LastLogon)),
        ];
    }

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

    private void LoadComputer(OnPremComputer? computer)
    {
        if (computer is null) { NotFound("computer"); return; }

        MissingReason = null;
        Name = computer.Name;
        Subtitle = Or(computer.OperatingSystem);
        DistinguishedName = computer.DistinguishedName;
        Location = DistinguishedNames.Friendly(computer.DistinguishedName);
        HasAccountState = true;
        IsEnabled = computer.Enabled;

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
