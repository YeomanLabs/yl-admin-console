using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>The three phases of UX §8: choose, watch, then read what happened.</summary>
public enum OffboardPhase
{
    Preview,
    Running,
    Summary,
}

/// <summary>
/// The offboard dialog (UX §8). One dialog, three phases: a checklist that says in concrete terms what each step will
/// change, the same list turning into a live log, then a summary with undo for the reversible steps.
/// </summary>
public sealed partial class OffboardDialogViewModel : DialogViewModelBase
{
    private readonly OffboardingRunner _runner;
    private readonly OffboardingProfileStore _profiles;
    private readonly PermissionChecker _permissions;
    private readonly ConsentService _consent;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;
    private readonly ILogger _logger;
    private readonly string _correlationId = $"offboard-{Guid.NewGuid():N}";

    private CancellationTokenSource? _cts;
    private OffboardingRun? _run;

    /// <summary>
    /// The context the last preview filled. The run must use this one: each step's preview leaves what it found in the
    /// context (the groups, the licences, the snapshot) and its execute acts on exactly that. Found in M15 — the run used
    /// to build a fresh, empty context, so those steps executed against nothing.
    /// </summary>
    private OffboardingContext? _previewed;
    private bool _undone;
    private IReadOnlyList<StepPreview> _previews = [];

    public OffboardDialogViewModel(
        DirectoryUser user,
        OffboardingRunner runner,
        OffboardingProfileStore profiles,
        PermissionChecker permissions,
        ConsentService consent,
        ActionLog actions,
        TenantContext tenant,
        ILogger logger)
    {
        User = user;
        _runner = runner;
        _profiles = profiles;
        _permissions = permissions;
        _consent = consent;
        _actions = actions;
        _tenant = tenant;
        _logger = logger;

        Profiles = _profiles.Load(_runner.Steps);
        _selectedProfile = OffboardingProfileStore.DefaultOf(Profiles);
        _delegateToManager = _selectedProfile.Options.DelegateToManager;
        Rows = _runner.Steps.Select(s => new OffboardStepRow(s, _selectedProfile.IsEnabled(s))).ToList();
        _profileError = _profiles.LoadErrors.Count == 0 ? null : $"Could not read {string.Join(", ", _profiles.LoadErrors)}";
    }

    /// <summary>
    /// Starts filling the dialog and returns at once, so the dialog opens straight away and each step fills in as its
    /// preview finishes. It used to be filled before it was shown, and in a large tenant that meant minutes of nothing
    /// on screen while the slowest preview (a Full Access scan across shared mailboxes, say) ran.
    /// </summary>
    public void StartPreparing(OffboardingProfile? startWith = null) => _ = PrepareSafelyAsync(startWith);

    private async Task PrepareSafelyAsync(OffboardingProfile? startWith)
    {
        try
        {
            await PrepareAsync(startWith);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preparing the offboard dialog for {UserId} failed", User.Id);
            StatusText = $"The preview failed: {ex.Message}";
            IsPreviewing = false;
        }
    }

    /// <summary>
    /// Fills the dialog: the template (a list's previous person's, so person 2 of 5 is offboarded the same way as
    /// person 1), the delegate the template asks for, then every step's preview.
    /// </summary>
    public async Task PrepareAsync(OffboardingProfile? startWith = null)
    {
        if (startWith is not null && !IsRestore
            && Profiles.FirstOrDefault(p => p.FileName == startWith.FileName && p.Name == startWith.Name) is { } match
            && !ReferenceEquals(match, SelectedProfile))
        {
            _applyingTemplate = true;
            SelectedProfile = match;
            _applyingTemplate = false;
        }

        if (!IsRestore) await ApplyTemplateDelegateAsync();
        await RefreshPreviewAsync();
    }

    public DirectoryUser User { get; }

    public override string Title => (ListPosition is { } at ? $"{at} · " : string.Empty) + (IsRestore
        ? $"Restore {User.DisplayName ?? User.UserPrincipalName}"
        : $"Offboard {User.DisplayName ?? User.UserPrincipalName}");

    /// <summary>"2 of 5" when this is one person of a pasted list (M15.5).</summary>
    public string? ListPosition { get; init; }

    /// <summary>Whether the run started — what tells a list whether to go on to the next person.</summary>
    public bool HasRun => _run is not null;

    public override bool ShowFooter => false;

    public override double Width => 760;

    public IReadOnlyList<OffboardStepRow> Rows { get; }

    // ---- restore (M15.4) ------------------------------------------------------------------------------------------

    /// <summary>Set for a restore: the dialog runs the restore catalog against one of these, newest first.</summary>
    public IReadOnlyList<SnapshotChoice> Snapshots { get; init; } = [];

    /// <summary>Where the AD account may go back to; the first is "where the snapshot found it".</summary>
    public IReadOnlyList<OuChoice> OuChoices { get; init; } = [];

    public bool IsRestore => Snapshots.Count > 0;

    public bool HasOuChoices => OuChoices.Count > 1;

    /// <summary>The profile, delegate and save-profile chrome belong to an offboarding only.</summary>
    public bool ShowsOffboardChrome => IsPreview && !IsRestore;

    public bool ShowsRestoreChrome => IsPreview && IsRestore;

    [ObservableProperty]
    private SnapshotChoice? _selectedSnapshot;

    [ObservableProperty]
    private OuChoice? _selectedOu;

    /// <summary>The opener's first choice, set without a preview each: it previews once, straight after.</summary>
    public void ChooseRestoreTarget(SnapshotChoice snapshot, OuChoice ou)
    {
        _choosing = true;
        SelectedSnapshot = snapshot;
        SelectedOu = ou;
        _choosing = false;
    }

    private bool _choosing;

    partial void OnSelectedSnapshotChanged(SnapshotChoice? value)
    {
        if (!_choosing) _ = RefreshPreviewAsync();
    }

    partial void OnSelectedOuChanged(OuChoice? value)
    {
        if (!_choosing) _ = RefreshPreviewAsync();
    }

    // ---- templates (OffboardingProfile in the code and the file format) --------------------------------------------

    public IReadOnlyList<OffboardingProfile> Profiles { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemplateDescription))]
    private OffboardingProfile _selectedProfile;

    /// <summary>What the chosen template is for, under the drop-down; null when it says nothing.</summary>
    public string? TemplateDescription => string.IsNullOrWhiteSpace(SelectedProfile.Description) ? null : SelectedProfile.Description;

    private bool _applyingTemplate;

    /// <summary>Files under templates\offboarding that would not load; saying so beats showing a shorter list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileError))]
    private string? _profileError;

    public bool HasProfileError => ProfileError is not null;

    partial void OnSelectedProfileChanged(OffboardingProfile value)
    {
        foreach (var row in Rows)
        {
            if (!row.IsBlocked) row.IsEnabled = value.IsEnabled(row.Step) || row.Step.IsRequired;
        }

        DelegateToManager = value.Options.DelegateToManager;
        RefreshRunLabel();

        // The template also carries the options the previews are built from (the reply text, the stamps, the export
        // folder) and maybe a delegate, so a change means a fresh preview: the run acts on what was last previewed.
        if (!_applyingTemplate && !IsRestore) _ = ApplyTemplateAndPreviewAsync();
    }

    private async Task ApplyTemplateAndPreviewAsync()
    {
        await ApplyTemplateDelegateAsync();
        await RefreshPreviewAsync();
    }

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    /// <summary>
    /// Saves what is ticked as a template. A name that is already a template updates that one (keeping its description
    /// and whether it opens by default) rather than making a second template of the same name.
    /// </summary>
    [RelayCommand]
    private void SaveProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0) return;
        if (string.Equals(name, OffboardingProfile.StandardLeaverName, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = $"“{OffboardingProfile.StandardLeaverName}” is built in. Save under another name.";
            return;
        }

        var existing = Profiles.FirstOrDefault(p => !p.IsBuiltIn && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var profile = _profiles.Save(OffboardingProfile.From(existing?.Name ?? name, _runner.Steps, EnabledIds, SelectedProfile.Options with { DelegateToManager = DelegateToManager }) with
        {
            Description = existing?.Description,
            IsDefault = existing?.IsDefault ?? false,
            FileName = existing?.FileName,
        });
        Profiles = _profiles.Load(_runner.Steps);
        OnPropertyChanged(nameof(Profiles));

        // The ticks and options are the ones already previewed, so the new selection needs no second preview.
        _applyingTemplate = true;
        SelectedProfile = Profiles.FirstOrDefault(p => p.FileName == profile.FileName) ?? Profiles[0];
        _applyingTemplate = false;
        NewProfileName = string.Empty;
        StatusText = existing is null ? $"Saved the template {profile.Name}." : $"Updated the template {profile.Name}.";
    }

    // ---- delegate -------------------------------------------------------------------------------------------------

    /// <summary>Set by the shell: opens the palette restricted to users and calls back with the pick.</summary>
    public Action<string, Action<PersonRef>>? PickDelegate { get; set; }

    /// <summary>Set by the opener: reads the leaver's manager live, for a template that delegates to them.</summary>
    public Func<CancellationToken, Task<PersonRef?>>? ReadManager { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelegateLabel))]
    private PersonRef? _chosenDelegate;

    [ObservableProperty]
    private bool _delegateToManager = true;

    /// <summary>The delegate is the manager the template asked for, not someone the admin picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelegateLabel))]
    private bool _delegateIsManager;

    /// <summary>Why the template's delegate could not be filled in; null when it was, or was not asked for.</summary>
    [ObservableProperty]
    private string? _delegateNote;

    /// <summary>Set once the admin picks or clears the delegate: from then on a template change leaves it alone.</summary>
    private bool _delegateChosenByAdmin;

    public string DelegateLabel => ChosenDelegate is null ? "None chosen" : DelegateIsManager ? $"{ChosenDelegate.Label} (their manager)" : ChosenDelegate.Label;

    /// <summary>
    /// The delegate the template asks for: the leaver's manager, read live, or nobody. Never overrides the admin's own
    /// choice in this dialog.
    /// </summary>
    private async Task ApplyTemplateDelegateAsync()
    {
        if (_delegateChosenByAdmin) return;

        DelegateNote = null;
        if (!SelectedProfile.Options.DelegateToManager || ReadManager is null)
        {
            if (DelegateIsManager)
            {
                DelegateIsManager = false;
                ChosenDelegate = null;
            }

            return;
        }

        try
        {
            var manager = await ReadManager(CancellationToken.None);
            DelegateIsManager = manager is not null;
            ChosenDelegate = manager;
            if (manager is null) DelegateNote = "The template hands things to the leaver's manager, but they have none. Choose a delegate if the ticked steps need one.";
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Could not read the manager of {UserId}: {Code}", User.Id, ex.ErrorCode);
            DelegateIsManager = false;
            ChosenDelegate = null;
            DelegateNote = $"The template hands things to the leaver's manager, but their manager could not be read ({GraphErrorText.Friendly(ex)}). Choose a delegate.";
        }
    }

    [RelayCommand]
    private void ChooseDelegate() => PickDelegate?.Invoke("Choose who inherits ownership and access", person =>
    {
        _delegateChosenByAdmin = true;
        DelegateIsManager = false;
        DelegateNote = null;
        ChosenDelegate = person;
        _ = RefreshPreviewAsync();
    });

    [RelayCommand]
    private void ClearDelegate()
    {
        _delegateChosenByAdmin = true;
        DelegateIsManager = false;
        DelegateNote = null;
        ChosenDelegate = null;
        _ = RefreshPreviewAsync();
    }

    // ---- phase ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreview))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsSummary))]
    [NotifyPropertyChangedFor(nameof(ShowsOffboardChrome))]
    [NotifyPropertyChangedFor(nameof(ShowsRestoreChrome))]
    private OffboardPhase _phase = OffboardPhase.Preview;

    public bool IsPreview => Phase == OffboardPhase.Preview;
    public bool IsRunning => Phase == OffboardPhase.Running;
    public bool IsSummary => Phase == OffboardPhase.Summary;

    [ObservableProperty]
    private bool _isPreviewing;

    [ObservableProperty]
    private string _runLabel = "Run";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummaryText))]
    private string? _summaryText;

    public bool HasSummaryText => SummaryText is not null;

    [ObservableProperty]
    private string? _statusText;

    private IReadOnlyList<string> EnabledIds => Rows.Where(r => r.IsEnabled).Select(r => r.Id).ToList();

    private OffboardingContext Context() => new()
    {
        User = User,
        Tenant = _tenant,
        Delegate = ChosenDelegate,
        CorrelationId = _correlationId,
        AttributeStamps = SelectedProfile.Options.AttributeStamps,
        AutoReplyMessage = SelectedProfile.Options.AutoReplyMessage,
        ExportFolder = SelectedProfile.Options.ExportFolder,
        RestoreFrom = SelectedSnapshot?.Snapshot,
        RestoreOuDn = SelectedOu?.Dn,
    };

    // ---- preview --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Counts previews started. A template or delegate change starts a new preview while one may still be running, and
    /// only the newest may fill the checklist and become what the run acts on: an older one finishing last would
    /// otherwise run the previous template's options.
    /// </summary>
    private int _previewGeneration;

    /// <summary>Runs every enabled step's preview at once and fills the checklist with what would actually change.</summary>
    public async Task RefreshPreviewAsync()
    {
        if (Phase != OffboardPhase.Preview) return;

        var generation = ++_previewGeneration;
        IsPreviewing = true;
        StatusText = null;
        foreach (var row in Rows) row.IsPreviewing = true;
        RefreshWaitingOn();

        // Each step's row fills in as its preview is ready, and the line above the list names what is still running.
        var progress = new Progress<StepPreview>(p =>
        {
            if (generation != _previewGeneration) return;
            if (Rows.FirstOrDefault(r => r.Id == p.StepId) is { } row)
            {
                row.Apply(p);
                row.IsPreviewing = false;
            }

            RefreshWaitingOn();
        });

        try
        {
            // Every step is previewed, not only the ticked ones: a row with no preview would show no reason for being
            // greyed, and the admin could not tell a blocked step from one they simply had not ticked.
            var ctx = Context();
            var previews = await _runner.PreviewAsync(ctx, _runner.Steps.Select(s => s.Id).ToList(), CancellationToken.None, progress);
            if (generation != _previewGeneration) return;
            _previews = previews;
            _previewed = ctx;
            var byId = _previews.ToDictionary(p => p.StepId, StringComparer.Ordinal);
            foreach (var row in Rows)
            {
                if (byId.TryGetValue(row.Id, out var preview)) row.Apply(preview);
            }

            // A required step (the snapshot) that could not be previewed stops everything, and says so up front rather
            // than as the first line of a run that then does nothing.
            if (Rows.FirstOrDefault(r => r.Step.IsRequired && r.IsBlocked) is { } required)
            {
                StatusText = $"Nothing can run: {required.Title.ToLowerInvariant()} failed. {required.Reason}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offboarding preview for {UserId} failed", User.Id);
            if (generation == _previewGeneration) StatusText = $"The preview failed: {ex.Message}";
        }
        finally
        {
            // A superseded preview leaves the spinner to the newer one still running.
            if (generation == _previewGeneration)
            {
                IsPreviewing = false;
                foreach (var row in Rows) row.IsPreviewing = false;
                RefreshRunLabel();
            }
        }
    }

    /// <summary>"Working out what each step would change — still waiting on …", or null when nothing is running.</summary>
    [ObservableProperty]
    private string _waitingOn = "Working out what each step would change…";

    private void RefreshWaitingOn()
    {
        var running = Rows.Where(r => r.IsPreviewing).Select(r => r.Title).ToList();
        WaitingOn = running.Count switch
        {
            0 => "Working out what each step would change…",
            <= 3 => $"Working out what each step would change — still waiting on {string.Join(", ", running)}…",
            _ => $"Working out what each step would change — {running.Count} steps still running, including {string.Join(", ", running.Take(3))}…",
        };
    }

    private void RefreshRunLabel()
    {
        var count = Rows.Count(r => r.IsEnabled && !r.IsBlocked);
        var verb = IsRestore ? "Restore" : "Run";
        RunLabel = count == 1 ? $"{verb} 1 step" : $"{verb} {count} steps";
        OnPropertyChanged(nameof(CanRun));
    }

    public bool CanRun => !IsPreviewing && _previewed is not null && !Rows.Any(r => r.Step.IsRequired && r.IsBlocked) && Rows.Any(r => r.IsEnabled && !r.IsBlocked);

    /// <summary>The checklist changed; the footer count follows it.</summary>
    public void OnRowToggled() => RefreshRunLabel();

    // ---- run ------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task RunAsync()
    {
        if (!CanRun) return;

        // Consent for everything this run needs, once, before the first step rather than in the middle of it.
        foreach (var capability in Rows.Where(r => r.IsEnabled && !r.IsBlocked).Select(r => r.Step.RequiredCapability).OfType<Capability>().DistinctBy(c => c.Id))
        {
            var permission = _permissions.Check(capability);
            if (!permission.NeedsConsent) continue;
            try
            {
                permission = await _consent.EnsureAsync(capability, CancellationToken.None);
            }
            catch (SignInException)
            {
                StatusText = "Permission was not granted, so nothing was run.";
                return;
            }

            if (!permission.CanRun)
            {
                StatusText = permission.Reason ?? "Not permitted, so nothing was run.";
                return;
            }
        }

        Phase = OffboardPhase.Running;
        StatusText = null;
        foreach (var row in Rows) row.IsLocked = true;

        var chosen = _previews.Where(p => Rows.First(r => r.Id == p.StepId).IsEnabled).ToList();
        _cts = new CancellationTokenSource();

        // One correlation id for every action-log row this run writes, so the Activity page shows it as one thing.
        using (ActionLog.BeginCorrelation(_correlationId))
        {
            var progress = new Progress<StepProgress>(p => Rows.First(r => r.Id == p.StepId).Apply(p));
            _run = await _runner.ExecuteAsync(_previewed!, chosen, progress, _cts.Token);
        }

        foreach (var result in _run.Results) Rows.First(r => r.Id == result.StepId).Apply(result);
        foreach (var row in Rows.Where(r => !r.IsEnabled)) row.ResultMessage ??= null;

        SummaryText = _run.Summary;
        if (_run.Halted) StatusText = "The run stopped because a step that must succeed failed.";
        else if (_run.Cancelled) StatusText = "The run was cancelled.";
        Phase = OffboardPhase.Summary;
        OnPropertyChanged(nameof(CanUndoRun));
        _logger.LogInformation("Offboarding of {UserId}: {Summary}", User.Id, _run.Summary);
    }

    [RelayCommand]
    private void CancelRun()
    {
        _cts?.Cancel();
        StatusText = "Cancelling — the step already running will finish first.";
    }

    // ---- summary --------------------------------------------------------------------------------------------------

    /// <summary>Undo is offered once. Pressing it twice would replay an inverse that has already been applied.</summary>
    public bool CanUndoRun => _run?.CanUndo == true && !IsUndoing && !_undone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoRun))]
    private bool _isUndoing;

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_run is null || !_run.CanUndo) return;

        IsUndoing = true;
        StatusText = "Undoing…";
        try
        {
            using (ActionLog.BeginCorrelation(_correlationId))
            {
                var progress = new Progress<StepProgress>(p => Rows.First(r => r.Id == p.StepId).Apply(p));
                var undo = await _runner.UndoAsync(_run, progress, CancellationToken.None);
                StatusText = undo.Failed == 0
                    ? $"Undone: {undo.Reversed} step{(undo.Reversed == 1 ? string.Empty : "s")} reversed."
                    : $"Undone: {undo.Reversed} reversed, {undo.Failed} could not be.";
            }
        }
        finally
        {
            _undone = true;
            IsUndoing = false;
            OnPropertyChanged(nameof(CanUndoRun));
        }
    }

    [RelayCommand]
    private async Task RetryAsync(OffboardStepRow row)
    {
        if (row.Outcome != StepOutcome.Failed) return;

        row.IsRetrying = true;
        row.Outcome = StepOutcome.Running;
        try
        {
            using (ActionLog.BeginCorrelation(_correlationId))
            {
                // One context for the retry's preview and execute, for the same reason the run reuses the preview's.
                var ctx = Context();
                var preview = await row.Step.PreviewAsync(ctx, CancellationToken.None);
                if (!preview.CanRun)
                {
                    row.Apply(StepResult.Skipped(row.Id, preview.Reason ?? preview.Summary));
                    return;
                }

                var result = await row.Step.ExecuteAsync(ctx, CancellationToken.None);
                row.Apply(result);
                if (result.Outcome == StepOutcome.Success && _run is not null)
                {
                    // Keep the run's undo list honest: the retry is what actually changed things.
                    _run = _run with { Results = _run.Results.Select(r => r.StepId == row.Id ? result : r).ToList() };
                    _undone = false;
                    OnPropertyChanged(nameof(CanUndoRun));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retry of {StepId} failed", row.Id);
            row.Apply(StepResult.Failed(row.Id, ex.Message));
        }
        finally
        {
            row.IsRetrying = false;
            SummaryText = _run?.Summary;
        }
    }

    /// <summary>UX §8: "Copy log" puts the whole run on the clipboard as markdown.</summary>
    [RelayCommand]
    private void CopyLog()
    {
        StatusText = ClipboardText.TryCopy(BuildLog()) ? "Log copied to the clipboard." : "The clipboard is unavailable right now.";
    }

    internal string BuildLog()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Offboarding — {User.DisplayName ?? User.UserPrincipalName}");
        sb.AppendLine();
        sb.AppendLine($"- User: `{User.UserPrincipalName}` (`{User.Id}`)");
        sb.AppendLine($"- Run by: {_tenant.SignedInUpn}");
        sb.AppendLine($"- When: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        sb.AppendLine($"- Correlation: `{_correlationId}`");
        if (ChosenDelegate is { } d) sb.AppendLine($"- Delegate: {d.Label}");
        if (SummaryText is { } s) sb.AppendLine($"- Result: {s}");
        sb.AppendLine();
        sb.AppendLine("| Step | Outcome | Duration | Result |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var row in Rows.Where(r => r.HasRun || r.IsEnabled))
        {
            var outcome = row.Outcome?.ToString() ?? "Not run";
            sb.AppendLine($"| {row.Title} | {outcome} | {row.DurationText ?? "—"} | {row.ResultMessage ?? row.Summary ?? string.Empty} |");
        }

        return sb.ToString();
    }

    /// <summary>Closes the dialog. During a run this cancels first, so nothing is left going in the background.</summary>
    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        CancelCommand.Execute(null);
    }
}
