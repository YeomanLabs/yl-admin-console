using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using System.IO;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Templates;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// The Templates page (M7.2): two tabs. New users: the saved templates on the left, the raw JSON on the right — the
/// whole template rather than a form, because doc 07's file is the contract and an admin editing it should see exactly
/// what will be written; nothing is saved until it parses. Offboarding: <see cref="OffboardingTemplatesViewModel"/>.
/// </summary>
public sealed partial class TemplatesPageViewModel : ObservableObject, IPageViewModel, IRefreshable
{
    private readonly TemplateService _templates;
    private readonly FilePicker _files;
    private readonly CacheDb _db;
    private readonly TenantContext _tenant;
    private readonly ToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly ILogger<TemplatesPageViewModel> _logger;

    public TemplatesPageViewModel(TemplateService templates, TenantContext tenant, ToastService toasts, IDialogService dialogs, ILogger<TemplatesPageViewModel> logger,
        OffboardingProfileStore offboardingTemplates, OffboardingRunner offboarding, FilePicker files, CacheDb db)
    {
        _files = files;
        _db = db;
        _templates = templates;
        _tenant = tenant;
        _toasts = toasts;
        _dialogs = dialogs;
        _logger = logger;
        Offboarding = new OffboardingTemplatesViewModel(offboardingTemplates, offboarding.Steps, tenant, toasts, dialogs, files, logger);
        Reload();
    }

    public string Title => "Templates";

    /// <summary>The Offboarding tab: what each kind of leaver gets.</summary>
    public OffboardingTemplatesViewModel Offboarding { get; }

    /// <summary>0 is New users, 1 is Offboarding; set by whoever opens the page on a particular tab.</summary>
    [ObservableProperty]
    private int _selectedTab;

    /// <summary>Set by the shell: opens the palette restricted to users and calls back with the pick.</summary>
    public Action<string, Action<PersonRef>>? PickPerson { get; set; }

    // ---- the list -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTemplates))]
    private IReadOnlyList<UserTemplate> _templatesList = [];

    public bool HasTemplates => TemplatesList.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HybridNote))]
    private UserTemplate? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    public bool HasLoadError => LoadError is not null;

    /// <summary>Doc 07 §Hybrid: in a synced tenant, say where people made from the selected template will be created.</summary>
    public bool IsHybridTenant => _tenant.OnPremisesSyncEnabled;

    /// <summary>
    /// Since M17 a template with an AD part creates the person in AD and finishes the cloud half after Entra Connect;
    /// one without creates a cloud-only account. (Until 0.3.3 this still said on-prem creation was coming in Phase 2.)
    /// </summary>
    public string HybridNote => Selected switch
    {
        { IsHybrid: true } => "This tenant syncs from on-prem AD, and this template has an AD part: people made from it are created in AD, and their licences, groups and mailbox settings follow once Entra Connect has synced them.",
        not null => "This tenant syncs from on-prem AD, but this template has no AD part, so people made from it are cloud-only accounts. Snapshot a synced user with New from user… to make one that creates people in AD.",
        _ => "This tenant syncs from on-prem AD. A template snapshotted from a synced user creates people in AD; one without an AD part creates cloud-only accounts.",
    };

    private void Reload(string? keepFileName = null)
    {
        TemplatesList = _templates.Load();
        LoadError = _templates.LoadErrors.Count == 0 ? null : $"Could not read {string.Join(", ", _templates.LoadErrors)}";
        Select(TemplatesList.FirstOrDefault(t => t.FileName == keepFileName) ?? TemplatesList.FirstOrDefault());
    }

    public Task RefreshAsync(CancellationToken ct)
    {
        Reload(Selected?.FileName);
        Offboarding.Reload(Offboarding.Selected?.Profile.FileName, keepBuiltIn: Offboarding.IsBuiltIn);
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

    // ---- import and export ----------------------------------------------------------------------------------------

    /// <summary>The selected template as saved (not unsaved edits in the editor), as a file to take elsewhere.</summary>
    [RelayCommand]
    private void Export()
    {
        if (Selected is not { } template) return;
        var path = _files.SaveAs(TemplateTransfer.ExportFileName(template.Name, TemplateFileKind.User), TemplateFiles.Filter, "Export template");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, TemplateService.Export(template));
            _toasts.Show($"Exported {template.Name}" + (IsDirty ? " (as saved; your unsaved edits are not in the file)" : string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _toasts.Show($"Could not write the file: {ex.Message}");
        }
    }

    /// <summary>Every template, one file each, into a folder — for moving them to another PC.</summary>
    [RelayCommand]
    private void ExportAll()
    {
        if (TemplatesList.Count == 0) return;
        var folder = _files.PickFolder("Export every user template to a folder");
        if (folder is null) return;
        try
        {
            foreach (var t in TemplatesList) TemplateFiles.WriteNew(folder, TemplateTransfer.ExportFileName(t.Name, TemplateFileKind.User), TemplateService.Export(t));
            _toasts.Show($"Exported {TemplatesList.Count} template{(TemplatesList.Count == 1 ? string.Empty : "s")} to {folder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _toasts.Show($"Could not write the files: {ex.Message}");
        }
    }

    /// <summary>
    /// Template files into this tenant: each checked with the editor's rules, renamed on a clash, and checked against
    /// this tenant's cache for groups, licences and a manager it does not have — said in the summary, file by file.
    /// </summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        var paths = _files.OpenFiles(TemplateFiles.Filter, "Import user templates");
        if (paths.Count == 0) return;

        var lines = new List<string>();
        UserTemplate? last = null;
        var skus = _db.GetSkus().Select(s => s.SkuId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var file = Path.GetFileName(path);
            var (text, problem) = TemplateFiles.Read(path);
            if (text is null)
            {
                lines.Add($"{file}: not imported — {problem}");
                continue;
            }

            if (_templates.Import(text, out var error) is not { } imported)
            {
                lines.Add($"{file}: not imported — {error}");
                continue;
            }

            last = imported;
            var missing = TemplateTransfer.MissingHere(imported, id => _db.GetGroup(id) is not null, skus.Contains, id => _db.GetUser(id) is not null);
            lines.Add(missing.Count == 0 ? $"{file}: imported as {imported.Name}." : $"{file}: imported as {imported.Name}. {string.Join(" ", missing)}");
        }

        _logger.LogInformation("Imported {Count} of {Files} user template file(s)", lines.Count(l => l.Contains(": imported", StringComparison.Ordinal)), paths.Count);
        Reload(last?.FileName ?? Selected?.FileName);
        await _dialogs.ShowAsync(new NoticeDialogViewModel("Import user templates", string.Join(Environment.NewLine + Environment.NewLine, lines)));
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
