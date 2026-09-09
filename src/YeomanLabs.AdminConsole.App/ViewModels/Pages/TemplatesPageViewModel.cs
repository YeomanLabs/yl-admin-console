using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.App.ViewModels.Dialogs;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Templates;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// The Templates page (M7.2): the saved templates on the left, the raw JSON on the right. The editor is the whole
/// template rather than a form, because doc 07's file is the contract and an admin editing it should see exactly what
/// will be written. Nothing is saved until it parses.
/// </summary>
public sealed partial class TemplatesPageViewModel : ObservableObject, IPageViewModel, IRefreshable
{
    private readonly TemplateService _templates;
    private readonly TenantContext _tenant;
    private readonly ToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly ILogger<TemplatesPageViewModel> _logger;

    public TemplatesPageViewModel(TemplateService templates, TenantContext tenant, ToastService toasts, IDialogService dialogs, ILogger<TemplatesPageViewModel> logger)
    {
        _templates = templates;
        _tenant = tenant;
        _toasts = toasts;
        _dialogs = dialogs;
        _logger = logger;
        Reload();
    }

    public string Title => "Templates";

    /// <summary>Set by the shell: opens the palette restricted to users and calls back with the pick.</summary>
    public Action<string, Action<PersonRef>>? PickPerson { get; set; }

    // ---- the list -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTemplates))]
    private IReadOnlyList<UserTemplate> _templatesList = [];

    public bool HasTemplates => TemplatesList.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private UserTemplate? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    public bool HasLoadError => LoadError is not null;

    /// <summary>Doc 07 §Hybrid note: creation here is cloud-only, and a hybrid tenant should be told so, not blocked.</summary>
    public bool IsHybridTenant => _tenant.OnPremisesSyncEnabled;

    public string HybridNote => "This tenant syncs from on-prem AD. Users created here are cloud-only. On-prem creation arrives in Phase 2.";

    private void Reload(string? keepFileName = null)
    {
        TemplatesList = _templates.Load();
        LoadError = _templates.LoadErrors.Count == 0 ? null : $"Could not read {string.Join(", ", _templates.LoadErrors)}";
        Select(TemplatesList.FirstOrDefault(t => t.FileName == keepFileName) ?? TemplatesList.FirstOrDefault());
    }

    public Task RefreshAsync(CancellationToken ct)
    {
        Reload(Selected?.FileName);
        return Task.CompletedTask;
    }

    partial void OnSelectedChanged(UserTemplate? value) => LoadEditor(value);

    private void Select(UserTemplate? template)
    {
        Selected = template;
        LoadEditor(template);
    }

    // ---- the editor -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _json = string.Empty;

    private string _savedJson = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string? _validationError;

    public bool HasValidationError => ValidationError is not null;

    /// <summary>Unsaved edits, so the page can say so rather than losing them quietly on a selection change.</summary>
    public bool IsDirty => Json != _savedJson;

    public bool CanSave => HasSelection && ValidationError is null && IsDirty;

    private void LoadEditor(UserTemplate? template)
    {
        _savedJson = template is null ? string.Empty : TemplateService.Serialize(template);
        Json = _savedJson;
        ValidationError = null;
    }

    partial void OnJsonChanged(string value)
    {
        // Validate on every keystroke: the Save button is the answer to "is this file good", so it has to be current.
        ValidationError = value.Trim().Length == 0 ? "The template is empty." : (TemplateService.TryParse(value, out var error) is null ? error : null);
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave || Selected is null) return;
        if (TemplateService.TryParse(Json, out var error) is not { } parsed)
        {
            ValidationError = error;
            return;
        }

        // Keep the file it came from: renaming a template must not leave the old file behind as a duplicate.
        var saved = _templates.Save(parsed with { FileName = Selected.FileName });
        _toasts.Show($"Saved {saved.Name}");
        Reload(saved.FileName);
    }

    [RelayCommand]
    private void Revert()
    {
        Json = _savedJson;
        ValidationError = null;
    }

    // ---- create and delete ----------------------------------------------------------------------------------------

    /// <summary>Doc 07: a template is snapshotted from a real user, so this picks one and reads it live.</summary>
    [RelayCommand]
    private void NewFromUser() => PickPerson?.Invoke("Choose someone whose setup this template should copy", person => _ = SnapshotAsync(person));

    private async Task SnapshotAsync(PersonRef person)
    {
        var name = new TextPromptDialogViewModel("New template", $"Snapshotting the role of {person.Label}. What is this template called?", "e.g. Sales Rep — Madison")
        {
            Message = "The attributes, direct licences and cloud groups are copied. Names, sign-in details and anything personal are not.",
        };

        if (!await _dialogs.ShowAsync(name)) return;

        try
        {
            var template = await _templates.SnapshotAsync(name.Value.Trim(), person.Id, CancellationToken.None);
            var saved = _templates.Save(template);
            Reload(saved.FileName);
            _toasts.Show(template.ExcludedOnSnapshot.Count == 0
                ? $"Created {saved.Name}"
                : $"Created {saved.Name} · {template.ExcludedOnSnapshot.Count} item(s) left out, listed in the file");
            _logger.LogInformation("Created the template {Name} from {UserId}", saved.Name, person.Id);
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Snapshot of {UserId} failed: {Code}", person.Id, ex.ErrorCode);
            _toasts.Show(GraphErrorText.Friendly(ex));
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } template) return;

        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Delete {template.Name}?",
            Message = "The template file is removed from this machine. Users already created from it are untouched.",
            ConfirmLabel = "Delete template",
            Destructive = true,
        });

        if (!confirmed) return;

        _templates.Delete(template);
        _toasts.Show($"Deleted {template.Name}");
        Reload();
    }
}
