using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.Templates;
using AdminConsole.Core.Users;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>The four screens of doc 07 §Create-from-template flow.</summary>
public enum CreatePhase
{
    PickTemplate,
    Form,
    Running,
    Result,
}

/// <summary>
/// Create a user from a template (doc 07). Picks a template, fills the person-specific fields with the rules doing the
/// tedious part, checks the sign-in name is free while the admin types, then creates, licences, joins and links in one
/// go. The temporary password is shown once at the end and exists nowhere else.
/// </summary>
public sealed partial class CreateUserDialogViewModel : DialogViewModelBase
{
    private const int UniquenessDebounceMs = 300;

    private readonly TemplateService _templates;
    private readonly UserProvisioningService _provisioning;
    private readonly CacheDb _db;
    private readonly TenantContext _tenant;
    private readonly PermissionChecker _permissions;
    private readonly ConsentService _consent;
    private readonly ILogger _logger;
    private CancellationTokenSource? _pendingCheck;

    public CreateUserDialogViewModel(
        TemplateService templates,
        UserProvisioningService provisioning,
        CacheDb db,
        TenantContext tenant,
        PermissionChecker permissions,
        ConsentService consent,
        ILogger logger)
    {
        _templates = templates;
        _provisioning = provisioning;
        _db = db;
        _tenant = tenant;
        _permissions = permissions;
        _consent = consent;
        _logger = logger;

        Templates = _templates.Load()
            .OrderByDescending(t => t.LastUsed ?? DateTimeOffset.MinValue)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        TemplateError = _templates.LoadErrors.Count == 0 ? null : $"Could not read {string.Join(", ", _templates.LoadErrors)}";
        Domains = _tenant.VerifiedDomains;
        _domain = Domains.FirstOrDefault() ?? string.Empty;

        if (Templates.Count == 1) Choose(Templates[0]);
        else if (Templates.Count == 0) _phase = CreatePhase.PickTemplate;
    }

    public override string Title => Phase switch
    {
        CreatePhase.PickTemplate => "New user",
        CreatePhase.Result => Result?.Success == true ? "User created" : "User not created",
        _ => $"New user from {Template?.Name}",
    };

    public override bool ShowFooter => false;

    public override double Width => 760;

    /// <summary>Set by the shell: opens the palette restricted to users, for the manager.</summary>
    public Action<string, Action<PersonRef>>? PickPerson { get; set; }

    /// <summary>Set by the shell: opens the palette restricted to groups.</summary>
    public Action<Action<DirectoryGroup>>? PickGroup { get; set; }

    /// <summary>Set by the shell: opens the created user's page.</summary>
    public Action<string, string>? OpenUser { get; set; }

    // ---- phase ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickingTemplate))]
    [NotifyPropertyChangedFor(nameof(IsForm))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsResult))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private CreatePhase _phase = CreatePhase.PickTemplate;

    public bool IsPickingTemplate => Phase == CreatePhase.PickTemplate;
    public bool IsForm => Phase == CreatePhase.Form;
    public bool IsRunning => Phase == CreatePhase.Running;
    public bool IsResult => Phase == CreatePhase.Result;

    [ObservableProperty]
    private string? _statusText;

    // ---- template -------------------------------------------------------------------------------------------------

    public IReadOnlyList<UserTemplate> Templates { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTemplateError))]
    private string? _templateError;

    public bool HasTemplateError => TemplateError is not null;

    public bool HasNoTemplates => Templates.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private UserTemplate? _template;

    /// <summary>Doc 07 §Hybrid note, rewritten for M17: what kind of create this will be, said before the click.</summary>
    public bool IsHybridTenant => _tenant.OnPremisesSyncEnabled;

    public string HybridNote => CanCreateInAd
        ? CreateInAd
            ? $"Creates the account in Active Directory, in {Core.OnPrem.DistinguishedNames.FriendlyPath(Template!.OnPrem!.OuDn)}. Licences, cloud groups and mailbox settings follow by themselves once Entra Connect syncs it; progress is on Home."
            : "Creates a cloud-only account even though this template has an AD section. In a synced organisation that is usually a second identity for the same person."
        : Template?.IsHybrid == true
            ? $"This template creates in AD, but {(AdReason ?? "no domain controller is reachable")} — so the account would be cloud-only."
            : "This tenant syncs from on-prem AD, and this template has no AD section, so the user is cloud-only. Snapshot a template from a synced user to create in AD.";

    // ---- hybrid (M17.2) ----------------------------------------------------------------------------------------------

    /// <summary>Set by the shell: creates in AD and queues the cloud half (M17.2). Null when AD is not configured.</summary>
    public HybridProvisioningService? Hybrid { get; init; }

    /// <summary>Set by the shell: the AD domain's DNS name when a DC is reachable; null otherwise.</summary>
    public string? AdDomain { get; init; }

    /// <summary>Set by the shell: why AD can't be used right now, when it can't.</summary>
    public string? AdReason { get; init; }

    public bool CanCreateInAd => Template?.IsHybrid == true && Hybrid is not null && AdDomain is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HybridNote))]
    [NotifyPropertyChangedFor(nameof(ShowAdFields))]
    private bool _createInAd;

    public bool ShowAdFields => CanCreateInAd && CreateInAd;

    [ObservableProperty]
    private string _samAccountName = string.Empty;

    /// <summary>True once the admin edits the pre-Windows 2000 name; the rule stops overwriting it.</summary>
    private bool _samEdited;

    partial void OnSamAccountNameChanged(string value)
    {
        if (Template is { } t && value != HybridProvisioningService.SamFor(t, FirstName, LastName)) _samEdited = true;
        OnPropertyChanged(nameof(CanCreate));
    }

    public string AdUpnSuffix => Template?.OnPrem?.UpnSuffix ?? AdDomain ?? string.Empty;

    public string AdUpn => UpnLocal.Length == 0 ? string.Empty : $"{UpnLocal}@{AdUpnSuffix}";

    partial void OnCreateInAdChanged(bool value) => RefreshPreview();

    [RelayCommand]
    private void ChooseTemplate(UserTemplate template) => Choose(template);

    private void Choose(UserTemplate template)
    {
        Template = template;
        Phase = CreatePhase.Form;

        Fields =
        [
            new CreateFieldRow("Job title", "jobTitle", Attribute(template, "jobTitle")),
            new CreateFieldRow("Department", "department", Attribute(template, "department")),
            new CreateFieldRow("Office", "officeLocation", Attribute(template, "officeLocation")),
            new CreateFieldRow("Employee ID", "employeeId", null),
            new CreateFieldRow("Mobile phone", "mobilePhone", null),
        ];

        var skus = _db.GetSkus().ToDictionary(s => s.SkuId, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(template.Licenses.Select(l => l.SkuId), StringComparer.OrdinalIgnoreCase);
        Licenses = skus.Values
            .Where(s => s.IsEnabled || wanted.Contains(s.SkuId))
            .OrderByDescending(s => wanted.Contains(s.SkuId)).ThenBy(s => s.DisplayName)
            .Select(s => new CreateLicenseRow(s, wanted.Contains(s.SkuId), RefreshPreview))
            .ToList();

        Groups = new List<CreateGroupRow>(template.Groups.Select(g => new CreateGroupRow(g, RemoveGroup)));
        ManagerId = template.ManagerId;
        ManagerLabel = template.ManagerDisplayName ?? (template.ManagerId is null ? "None" : template.ManagerId);
        CreateInAd = CanCreateInAd;
        OnPropertyChanged(nameof(CanCreateInAd));
        OnPropertyChanged(nameof(HybridNote));
        OnPropertyChanged(nameof(AdUpnSuffix));
        RefreshNames();
    }

    private static string? Attribute(UserTemplate template, string key) =>
        template.Attributes.TryGetValue(key, out var v) ? v as string : null;

    // ---- the form (doc 07 §flow step 2) -----------------------------------------------------------------------------

    [ObservableProperty]
    private string _firstName = string.Empty;

    [ObservableProperty]
    private string _lastName = string.Empty;

    partial void OnFirstNameChanged(string value) => RefreshNames();
    partial void OnLastNameChanged(string value) => RefreshNames();

    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>True once the admin types their own display name; the rule stops overwriting it after that.</summary>
    private bool _displayNameEdited;

    partial void OnDisplayNameChanged(string value) => RefreshPreview();

    public void MarkDisplayNameEdited() => _displayNameEdited = true;

    [ObservableProperty]
    private string _upnLocal = string.Empty;

    private bool _upnEdited;

    public void MarkUpnEdited() => _upnEdited = true;

    partial void OnUpnLocalChanged(string value)
    {
        RefreshPreview();
        _ = CheckUniquenessAsync();
    }

    public IReadOnlyList<string> Domains { get; }

    [ObservableProperty]
    private string _domain;

    partial void OnDomainChanged(string value)
    {
        RefreshPreview();
        _ = CheckUniquenessAsync();
    }

    public string Upn => UpnLocal.Length == 0 ? string.Empty : $"{UpnLocal}@{Domain}";

    private void RefreshNames()
    {
        if (Template is not { } template) return;

        if (!_displayNameEdited) DisplayName = NameRules.ExpandDisplay(template.DisplayNameRule, FirstName, LastName);
        if (!_upnEdited)
        {
            var full = NameRules.ExpandAddress(template.UpnRule, FirstName, LastName, Domain);
            UpnLocal = full.Contains('@', StringComparison.Ordinal) ? full[..full.IndexOf('@', StringComparison.Ordinal)] : full;
        }

        RefreshPreview();
    }

    [ObservableProperty]
    private IReadOnlyList<CreateFieldRow> _fields = [];

    [ObservableProperty]
    private IReadOnlyList<CreateLicenseRow> _licenses = [];

    [ObservableProperty]
    private List<CreateGroupRow> _groups = [];

    [ObservableProperty]
    private string? _managerId;

    [ObservableProperty]
    private string _managerLabel = "None";

    [RelayCommand]
    private void ChooseManager() => PickPerson?.Invoke("Choose this person's manager", person =>
    {
        ManagerId = person.Id;
        ManagerLabel = person.Label;
        RefreshPreview();
    });

    [RelayCommand]
    private void ClearManager()
    {
        ManagerId = null;
        ManagerLabel = "None";
        RefreshPreview();
    }

    [RelayCommand]
    private void AddGroup() => PickGroup?.Invoke(group =>
    {
        if (Groups.Any(g => g.Id == group.Id)) return;
        Groups = [.. Groups, new CreateGroupRow(new TemplateGroup { Id = group.Id, DisplayName = group.DisplayName }, RemoveGroup)];
        RefreshPreview();
    });

    private void RemoveGroup(CreateGroupRow row)
    {
        Groups = Groups.Where(g => g.Id != row.Id).ToList();
        RefreshPreview();
    }

    // ---- uniqueness (doc 07: debounced 300 ms) -----------------------------------------------------------------------

    [ObservableProperty]
    private bool _isCheckingUpn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpnMessage))]
    [NotifyPropertyChangedFor(nameof(UpnIsBad))]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private string? _upnMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpnIsBad))]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool _upnBlocked;

    public bool HasUpnMessage => UpnMessage is not null;
    public bool UpnIsBad => UpnBlocked;

    private async Task CheckUniquenessAsync()
    {
        _pendingCheck?.Cancel();
        var cts = _pendingCheck = new CancellationTokenSource();
        UpnMessage = null;
        UpnBlocked = false;

        if (Upn.Length == 0) return;

        try
        {
            await Task.Delay(UniquenessDebounceMs, cts.Token);
            IsCheckingUpn = true;

            var upn = await _provisioning.CheckUpnAsync(Upn, cts.Token);
            if (cts.IsCancellationRequested) return;

            if (upn.BlocksCreate)
            {
                UpnMessage = upn.Message;
                UpnBlocked = true;
                return;
            }

            // The mail collision matters just as much, and it is a different query.
            var mail = await _provisioning.CheckProxyAddressAsync(Upn, cts.Token);
            if (cts.IsCancellationRequested) return;

            UpnBlocked = mail.BlocksCreate;
            UpnMessage = mail.BlocksCreate ? mail.Message : upn.Message ?? mail.Message ?? "That sign-in name is free.";
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsCheckingUpn = false;
        }
    }

    // ---- preview (doc 07 §flow step 3) -------------------------------------------------------------------------------

    [ObservableProperty]
    private string _previewText = string.Empty;

    public bool CanCreate =>
        Phase == CreatePhase.Form
        && FirstName.Trim().Length > 0
        && LastName.Trim().Length > 0
        && DisplayName.Trim().Length > 0
        && UpnLocal.Trim().Length > 0
        && Domain.Length > 0
        && !UpnBlocked
        && (!ShowAdFields || SamAccountName.Trim().Length is > 0 and <= 20);

    private void RefreshPreview()
    {
        if (Template is { } t && !_samEdited) SamAccountName = HybridProvisioningService.SamFor(t, FirstName, LastName);
        OnPropertyChanged(nameof(AdUpn));
        var licences = Licenses.Count(l => l.IsSelected);
        var parts = new List<string>(4)
        {
            ShowAdFields
                ? $"Create {(DisplayName.Trim().Length == 0 ? "a user" : DisplayName.Trim())} in AD as {(AdUpn.Length == 0 ? "…" : AdUpn)} ({SamAccountName})"
                : $"Create {(DisplayName.Trim().Length == 0 ? "a user" : DisplayName.Trim())} as {(Upn.Length == 0 ? "…" : Upn)}",
        };
        if (ShowAdFields && Template?.OnPrem is { } onPrem) parts.Add($"{onPrem.Groups.Count} AD group{(onPrem.Groups.Count == 1 ? string.Empty : "s")}");

        if (UsageLocation is { } usage) parts.Add($"usage location {usage}");
        parts.Add($"{licences} licence{(licences == 1 ? string.Empty : "s")}");
        parts.Add($"{Groups.Count} group{(Groups.Count == 1 ? string.Empty : "s")}");
        if (ManagerId is not null) parts.Add($"manager {ManagerLabel}");

        PreviewText = string.Join(" · ", parts);
        OnPropertyChanged(nameof(CanCreate));
    }

    private string? UsageLocation => Template is null ? null : Attribute(Template, "usageLocation");

    // ---- execute (doc 07 §flow step 4) -------------------------------------------------------------------------------

    [ObservableProperty]
    private CreateUserResult? _result;

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate || Template is not { } template) return;

        foreach (var capability in new[] { Capabilities.CreateUser, Capabilities.AssignLicense, Capabilities.ChangeGroupMembership })
        {
            var permission = _permissions.Check(capability);
            if (!permission.NeedsConsent) continue;
            try
            {
                permission = await _consent.EnsureAsync(capability, CancellationToken.None);
            }
            catch (SignInException)
            {
                StatusText = "Permission was not granted, so nothing was created.";
                return;
            }

            if (!permission.CanRun)
            {
                StatusText = permission.Reason ?? "Not permitted, so nothing was created.";
                return;
            }
        }

        Phase = CreatePhase.Running;
        StatusText = null;

        var attributes = new Dictionary<string, object?>(template.Attributes, StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (field.Trimmed is { } value) attributes[field.PropertyName] = value;
            else attributes.Remove(field.PropertyName);
        }

        var request = new CreateUserRequest
        {
            DisplayName = DisplayName.Trim(),
            GivenName = FirstName.Trim(),
            Surname = LastName.Trim(),
            UserPrincipalName = Upn,
            MailNickname = NameRules.ExpandAddress(template.MailNicknameRule, FirstName, LastName),
            Attributes = attributes,
            ManagerId = ManagerId,
            SkuIds = Licenses.Where(l => l.IsSelected).Select(l => l.SkuId).ToList(),
            Groups = Groups.Select(g => g.Group).ToList(),
            Password = UserService.GeneratePassword(template.PasswordPolicy.Length),
            ForceChangePasswordNextSignIn = template.PasswordPolicy.ForceChangeOnFirstSignIn,
        };

        if (ShowAdFields && Hybrid is { } hybrid && template.OnPrem is { } onPremTemplate)
        {
            // The cloud address becomes the AD mail attribute: Entra Connect carries it up as the primary SMTP address.
            attributes["mail"] = Upn;
            request = request with { Attributes = attributes };
            Result = await hybrid.CreateAsync(template, request, onPremTemplate.OuDn, SamAccountName.Trim(), AdUpn, CancellationToken.None);
        }
        else
        {
            Result = await _provisioning.CreateAsync(request, CancellationToken.None);
        }
        Phase = CreatePhase.Result;
        OnPropertyChanged(nameof(HasResultPassword));
        OnPropertyChanged(nameof(ResultSummary));

        if (Result.Success) _templates.MarkUsed(template);
        _logger.LogInformation("Create from template {Template}: {Summary}", template.Name, Result.Summary);
    }

    // ---- result (doc 07 §flow step 5) --------------------------------------------------------------------------------

    public bool HasResultPassword => Result?.Password is not null;

    public string ResultSummary => Result?.Summary ?? string.Empty;

    public string SendHint => "There is no email sending in this version — pass it on the way you normally would.";

    [ObservableProperty]
    private bool _isPasswordCopied;

    [RelayCommand]
    private void CopyPassword() => IsPasswordCopied = ClipboardText.TryCopy(Result?.Password);

    [RelayCommand]
    private void OpenCreatedUser()
    {
        if (Result is { Success: true, UserId: { } id }) OpenUser?.Invoke(id, DisplayName.Trim());
        CancelCommand.Execute(null);
    }

    /// <summary>"Create another": same template, empty form, and the password from the last one is gone.</summary>
    [RelayCommand]
    private void CreateAnother()
    {
        Result = null;
        IsPasswordCopied = false;
        FirstName = string.Empty;
        LastName = string.Empty;
        _displayNameEdited = false;
        _upnEdited = false;
        DisplayName = string.Empty;
        UpnLocal = string.Empty;
        _samEdited = false;
        UpnMessage = null;
        UpnBlocked = false;
        StatusText = null;
        if (Template is { } template) Choose(template);
    }

    [RelayCommand]
    private void Close() => CancelCommand.Execute(null);
}
