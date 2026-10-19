using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.Templates;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// The Offboarding half of the Templates page: which steps a kind of leaver gets, and what they run with — the
/// delegate, the automatic reply, the attribute stamps, the OneDrive export folder. A form rather than the JSON editor
/// user templates have, because every value here is a choice from a fixed set, and a form cannot name a step that does
/// not exist. "Standard leaver" is built in and read-only; duplicate it to change it.
/// </summary>
public sealed partial class OffboardingTemplatesViewModel : ObservableObject
{
    private const string StampStepId = "update-attributes";
    private const string AutoReplyStepId = "set-auto-reply";
    private const string ExportStepId = "export-onedrive";

    private readonly OffboardingProfileStore _store;
    private readonly IReadOnlyList<IOffboardingStep> _steps;
    private readonly TenantContext _tenant;
    private readonly ToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly FilePicker _files;
    private readonly ILogger _logger;

    private IReadOnlyList<OffboardingProfile> _profiles = [];
    private string _savedJson = string.Empty;
    private bool _loading;

    public OffboardingTemplatesViewModel(OffboardingProfileStore store, IReadOnlyList<IOffboardingStep> steps, TenantContext tenant, ToastService toasts, IDialogService dialogs, FilePicker files, ILogger logger)
    {
        _store = store;
        _steps = steps;
        _tenant = tenant;
        _toasts = toasts;
        _dialogs = dialogs;
        _files = files;
        _logger = logger;
        Reload();
    }

    // ---- the list -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<OffboardingTemplateItem> _items = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsBuiltIn), nameof(IsEditable), nameof(CanDelete), nameof(CanMakeDefault))]
    private OffboardingTemplateItem? _selected;

    public bool HasSelection => Selected is not null;

    public bool IsBuiltIn => Selected?.Profile.IsBuiltIn == true;

    public bool IsEditable => HasSelection && !IsBuiltIn;

    public bool CanDelete => IsEditable;

    public bool CanMakeDefault => Selected is { IsDefault: false };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    public bool HasLoadError => LoadError is not null;

    public void Reload(string? keepFileName = null, bool keepBuiltIn = false)
    {
        _profiles = _store.Load(_steps);
        var defaultOne = OffboardingProfileStore.DefaultOf(_profiles);
        Items = _profiles.Select(p => new OffboardingTemplateItem(p, p.EnabledIds(_steps).Count(id => _steps.First(s => s.Id == id) is not NotInThisPhaseStep), ReferenceEquals(p, defaultOne))).ToList();
        LoadError = _store.LoadErrors.Count == 0 ? null : $"Could not read {string.Join(", ", _store.LoadErrors)}";

        var keep = keepBuiltIn
            ? Items[0]
            : Items.FirstOrDefault(i => keepFileName is not null && i.Profile.FileName == keepFileName);
        Selected = keep ?? Items.FirstOrDefault(i => i.IsDefault) ?? Items[0];
    }

    partial void OnSelectedChanged(OffboardingTemplateItem? value) => LoadEditor(value?.Profile);

    // ---- the editor -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<TemplateStepGroup> _groups = [];

    [ObservableProperty]
    private bool _delegateToManager;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoReplySample))]
    private string _autoReply = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<StampRow> _stamps = [];

    [ObservableProperty]
    private string _exportFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(CanSave))]
    private IReadOnlyList<string> _problems = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isDirty;

    public bool CanSave => IsEditable && IsDirty && !HasProblems;

    /// <summary>The placeholders, for the line under the reply box.</summary>
    public string TokenHelp => "Placeholders: " + string.Join(" · ", AutoReplyText.Tokens.Select(t => $"{t.Token} {t.Meaning}"));

    /// <summary>The reply as a leaver would send it, with sample people, so the admin can read it before it is used.</summary>
    public string AutoReplySample
    {
        get
        {
            var sample = new AutoReplyText.Values
            {
                Name = "Adele Vance",
                FirstName = "Adele",
                Email = "adele@contoso.com",
                Delegate = "Megan Bowen",
                DelegateEmail = "megan@contoso.com",
                Company = string.IsNullOrWhiteSpace(_tenant.TenantDisplayName) ? "our organisation" : _tenant.TenantDisplayName,
            };
            var text = AutoReply.Trim().Length == 0
                ? $"Thank you for your message. {sample.Name} is no longer with us; please contact {sample.Delegate} ({sample.DelegateEmail}) instead."
                : AutoReplyText.Render(AutoReply.Trim(), sample);
            return $"With Adele Vance leaving and Megan Bowen as the delegate: “{text}”";
        }
    }

    /// <summary>Notes on the option sections whose step this template does not tick, so nobody tunes a dead setting.</summary>
    [ObservableProperty]
    private string? _stampNote;

    [ObservableProperty]
    private string? _autoReplyNote;

    [ObservableProperty]
    private string? _exportNote;

    private IEnumerable<TemplateStepRow> Rows => Groups.SelectMany(g => g.Rows);

    private void LoadEditor(OffboardingProfile? profile)
    {
        _loading = true;
        try
        {
            Name = profile?.Name ?? string.Empty;
            Description = profile?.Description ?? string.Empty;
            Groups = profile is null ? [] : TemplateStepGroup.From(_steps.Select(s => new TemplateStepRow(s, profile.IsEnabled(s), Recheck)));
            DelegateToManager = profile?.Options.DelegateToManager ?? false;
            AutoReply = profile?.Options.AutoReplyMessage ?? string.Empty;
            ExportFolder = profile?.Options.ExportFolder ?? string.Empty;

            // An empty set means doc 06's defaults, so that is what the form shows.
            var stamps = profile is null ? new Dictionary<string, string?>()
                : profile.Options.AttributeStamps.Count > 0 ? profile.Options.AttributeStamps : UpdateAttributesStep.Defaults;
            Stamps = OffboardingProfile.StampableAttributes
                .Select(a => new StampRow(a, stamps.GetValueOrDefault(a), stamps.ContainsKey(a), Recheck))
                .ToList();
        }
        finally
        {
            _loading = false;
        }

        _savedJson = profile is null || profile.IsBuiltIn ? string.Empty : Serialize(Build());
        Recheck();
    }

    partial void OnNameChanged(string value) => Recheck();
    partial void OnDescriptionChanged(string value) => Recheck();
    partial void OnDelegateToManagerChanged(bool value) => Recheck();
    partial void OnAutoReplyChanged(string value) => Recheck();
    partial void OnExportFolderChanged(string value) => Recheck();

    /// <summary>Every edit: what would be saved, whether it differs from the file, and what stops it being saved.</summary>
    private void Recheck()
    {
        if (_loading) return;
        if (!IsEditable)
        {
            Problems = [];
            IsDirty = false;
            RefreshNotes();
            return;
        }

        var built = Build();
        var problems = built.Problems(_profiles).ToList();
        var ticked = Rows.Where(r => r.IsChecked).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var row in Stamps.Where(s => s.IsIncomplete))
        {
            problems.Add($"Type what to set {row.Label.ToLowerInvariant()} to, or choose “Clear it”.");
        }

        if (ticked.Contains(StampStepId) && Stamps.All(s => s.Mode == StampMode.Leave))
        {
            problems.Add("Stamp at least one attribute, or untick “Stamp offboarding attributes”.");
        }

        Problems = problems;
        IsDirty = Serialize(built) != _savedJson;
        RefreshNotes();
    }

    private void RefreshNotes()
    {
        var ticked = Rows.Where(r => r.IsChecked).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        StampNote = ticked.Contains(StampStepId) ? null : "“Stamp offboarding attributes” is not ticked, so these are used only if it is ticked in the offboard dialog.";
        AutoReplyNote = ticked.Contains(AutoReplyStepId) ? null : "“Set an automatic reply” is not ticked, so this is used only if it is ticked in the offboard dialog.";
        ExportNote = ticked.Contains(ExportStepId)
            ? ExportFolder.Trim().Length > 0 && !Directory.Exists(ExportFolder.Trim()) ? "That folder does not exist on this PC, so the export will be blocked until it does." : null
            : "“Export their OneDrive” is not ticked, so this is used only if it is ticked in the offboard dialog.";
    }

    /// <summary>The template the form describes, keeping its file and its default mark.</summary>
    private OffboardingProfile Build()
    {
        var original = Selected?.Profile;
        var stamps = Stamps
            .Where(s => s.Mode != StampMode.Leave)
            .ToDictionary(s => s.Attribute, s => (string?)(s.Mode == StampMode.Clear ? string.Empty : s.Value.Trim()), StringComparer.Ordinal);
        var options = (original?.Options ?? new OffboardingOptions()) with
        {
            DelegateToManager = DelegateToManager,
            AttributeStamps = stamps,
            AutoReplyMessage = AutoReply.Trim().Length == 0 ? null : AutoReply.Trim(),
            ExportFolder = ExportFolder.Trim().Length == 0 ? null : ExportFolder.Trim(),
        };

        return OffboardingProfile.From(Name.Trim(), _steps, Rows.Where(r => r.IsChecked).Select(r => r.Id).ToList(), options) with
        {
            Description = Description.Trim().Length == 0 ? null : Description.Trim(),
            IsDefault = original?.IsDefault ?? false,
            FileName = original?.FileName,
        };
    }

    private static string Serialize(OffboardingProfile profile) => JsonSerializer.Serialize(profile, JsonFiles.Options);

    // ---- commands -------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;

        var saved = _store.Save(Build());
        _logger.LogInformation("Saved the offboarding template {File}", saved.FileName);
        _toasts.Show($"Saved {saved.Name}");
        Reload(saved.FileName);
    }

    [RelayCommand]
    private void Revert() => LoadEditor(Selected?.Profile);

    /// <summary>A new template starts as "Standard leaver" does, under a name of its own, and is saved straight away.</summary>
    [RelayCommand]
    private void New() => Create(OffboardingProfile.StandardLeaver(_steps), "New offboarding template", description: null);

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } item) return;
        Create(item.Profile, $"{item.Name} (copy)", item.Profile.Description);
    }

    private void Create(OffboardingProfile basis, string name, string? description)
    {
        var saved = _store.Save(basis with { Name = UniqueName(name), Description = description, IsDefault = false, FileName = null });
        _logger.LogInformation("Created the offboarding template {File}", saved.FileName);
        _toasts.Show($"Created {saved.Name}");
        Reload(saved.FileName);
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var n = 2; _profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++) candidate = $"{name} {n}";
        return candidate;
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { Profile: { IsBuiltIn: false } profile } item) return;

        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Delete {item.Name}?",
            Message = item.IsDefault
                ? "The template file is removed from this PC, and the offboard dialog will open with Standard leaver instead. People already offboarded with it are untouched."
                : "The template file is removed from this PC. People already offboarded with it are untouched.",
            ConfirmLabel = "Delete template",
            Destructive = true,
        });

        if (!confirmed) return;

        _store.Delete(profile);
        _logger.LogInformation("Deleted the offboarding template {File}", profile.FileName);
        _toasts.Show($"Deleted {item.Name}");
        Reload();
    }

    /// <summary>The offboard dialog opens with this one. Takes effect at once, like Delete: there is nothing to review.</summary>
    [RelayCommand]
    private void MakeDefault()
    {
        if (Selected is not { IsDefault: false } item) return;
        if (IsDirty)
        {
            _toasts.Show("Save or revert your changes first.");
            return;
        }

        _store.SetDefault(_profiles, item.Profile);
        _toasts.Show($"The offboard dialog now opens with {item.Name}");
        Reload(item.Profile.FileName, keepBuiltIn: item.Profile.IsBuiltIn);
    }

    // ---- import and export ----------------------------------------------------------------------------------------

    /// <summary>A saved template (not the built-in, which every installation already has) as a file.</summary>
    [RelayCommand]
    private void Export()
    {
        if (Selected is not { Profile: { IsBuiltIn: false } profile }) return;
        var path = _files.SaveAs(TemplateTransfer.ExportFileName(profile.Name, TemplateFileKind.Offboarding), TemplateFiles.Filter, "Export offboarding template");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, OffboardingProfileStore.Export(profile));
            _toasts.Show($"Exported {profile.Name}" + (IsDirty ? " (as saved; your unsaved changes are not in the file)" : string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _toasts.Show($"Could not write the file: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ExportAll()
    {
        var saved = _profiles.Where(p => !p.IsBuiltIn).ToList();
        if (saved.Count == 0)
        {
            _toasts.Show("There are no offboarding templates of your own to export yet.");
            return;
        }

        var folder = _files.PickFolder("Export every offboarding template to a folder");
        if (folder is null) return;
        try
        {
            foreach (var p in saved) TemplateFiles.WriteNew(folder, TemplateTransfer.ExportFileName(p.Name, TemplateFileKind.Offboarding), OffboardingProfileStore.Export(p));
            _toasts.Show($"Exported {saved.Count} offboarding template{(saved.Count == 1 ? string.Empty : "s")} to {folder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _toasts.Show($"Could not write the files: {ex.Message}");
        }
    }

    /// <summary>Offboarding template files: refused when they break the editor's rules, renamed on a clash, never the default.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        var paths = _files.OpenFiles(TemplateFiles.Filter, "Import offboarding templates");
        if (paths.Count == 0) return;

        var lines = new List<string>();
        OffboardingProfile? last = null;
        foreach (var path in paths)
        {
            var file = Path.GetFileName(path);
            var (text, problem) = TemplateFiles.Read(path);
            if (text is null)
            {
                lines.Add($"{file}: not imported — {problem}");
                continue;
            }

            if (_store.Import(text, _store.Load(_steps), out var error) is not { } imported)
            {
                lines.Add($"{file}: not imported — {error}");
                continue;
            }

            last = imported;
            lines.Add($"{file}: imported as {imported.Name}.");
        }

        _logger.LogInformation("Imported {Count} of {Files} offboarding template file(s)", lines.Count(l => l.Contains(": imported", StringComparison.Ordinal)), paths.Count);
        Reload(last?.FileName ?? Selected?.Profile.FileName);
        await _dialogs.ShowAsync(new NoticeDialogViewModel("Import offboarding templates", string.Join(Environment.NewLine + Environment.NewLine, lines)));
    }

    [RelayCommand]
    private void BrowseExportFolder()
    {
        if (!IsEditable) return;
        if (_files.PickFolder("Where OneDrive exports are saved", ExportFolder) is { } folder) ExportFolder = folder;
    }
}
