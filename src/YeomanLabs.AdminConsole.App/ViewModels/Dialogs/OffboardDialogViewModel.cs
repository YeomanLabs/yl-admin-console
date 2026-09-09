using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Offboarding;

namespace YeomanLabs.AdminConsole.App.ViewModels.Dialogs;

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
        _selectedProfile = Profiles[0];
        Rows = _runner.Steps.Select(s => new OffboardStepRow(s, _selectedProfile.IsEnabled(s))).ToList();
        _profileError = _profiles.LoadErrors.Count == 0 ? null : $"Could not read {string.Join(", ", _profiles.LoadErrors)}";
    }

    public DirectoryUser User { get; }

    public override string Title => $"Offboard {User.DisplayName ?? User.UserPrincipalName}";

    public override bool ShowFooter => false;

    public override double Width => 760;

    public IReadOnlyList<OffboardStepRow> Rows { get; }

    // ---- profiles -------------------------------------------------------------------------------------------------

    public IReadOnlyList<OffboardingProfile> Profiles { get; private set; }

    [ObservableProperty]
    private OffboardingProfile _selectedProfile;

    /// <summary>Files under templates\offboarding that would not load; saying so beats showing a shorter list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileError))]
    private string? _profileError;

    public bool HasProfileError => ProfileError is not null;

    partial void OnSelectedProfileChanged(OffboardingProfile value)
    {
        foreach (var row in Rows)
        {
            if (!row.IsBlocked) row.IsEnabled = value.IsEnabled(row.Step);
        }

        DelegateToManager = value.Options.DelegateToManager;
        RefreshRunLabel();
    }

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [RelayCommand]
    private void SaveProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0) return;

        var profile = _profiles.Save(OffboardingProfile.From(name, _runner.Steps, EnabledIds, new OffboardingOptions { DelegateToManager = DelegateToManager }));
        Profiles = _profiles.Load(_runner.Steps);
        OnPropertyChanged(nameof(Profiles));
        SelectedProfile = Profiles.FirstOrDefault(p => p.FileName == profile.FileName) ?? Profiles[0];
        NewProfileName = string.Empty;
    }

    // ---- delegate -------------------------------------------------------------------------------------------------

    /// <summary>Set by the shell: opens the palette restricted to users and calls back with the pick.</summary>
    public Action<Action<PersonRef>>? PickDelegate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelegateLabel))]
    private PersonRef? _chosenDelegate;

    [ObservableProperty]
    private bool _delegateToManager = true;

    public string DelegateLabel => ChosenDelegate?.Label ?? "None chosen";

    [RelayCommand]
    private void ChooseDelegate() => PickDelegate?.Invoke(person =>
    {
        ChosenDelegate = person;
        _ = RefreshPreviewAsync();
    });

    [RelayCommand]
    private void ClearDelegate()
    {
        ChosenDelegate = null;
        _ = RefreshPreviewAsync();
    }

    // ---- phase ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreview))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsSummary))]
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
    };

    // ---- preview --------------------------------------------------------------------------------------------------

    /// <summary>Runs every enabled step's preview at once and fills the checklist with what would actually change.</summary>
    public async Task RefreshPreviewAsync()
    {
        if (Phase != OffboardPhase.Preview) return;

        IsPreviewing = true;
        StatusText = null;
        foreach (var row in Rows) row.IsPreviewing = true;

        try
        {
            // Every step is previewed, not only the ticked ones: a row with no preview would show no reason for being
            // greyed, and the admin could not tell a blocked step from one they simply had not ticked.
            _previews = await _runner.PreviewAsync(Context(), _runner.Steps.Select(s => s.Id).ToList(), CancellationToken.None);
            var byId = _previews.ToDictionary(p => p.StepId, StringComparer.Ordinal);
            foreach (var row in Rows)
            {
                if (byId.TryGetValue(row.Id, out var preview)) row.Apply(preview);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offboarding preview for {UserId} failed", User.Id);
            StatusText = $"The preview failed: {ex.Message}";
        }
        finally
        {
            IsPreviewing = false;
            foreach (var row in Rows) row.IsPreviewing = false;
            RefreshRunLabel();
        }
    }

    private void RefreshRunLabel()
    {
        var count = Rows.Count(r => r.IsEnabled && !r.IsBlocked);
        RunLabel = count == 1 ? "Run 1 step" : $"Run {count} steps";
        OnPropertyChanged(nameof(CanRun));
    }

    public bool CanRun => !IsPreviewing && Rows.Any(r => r.IsEnabled && !r.IsBlocked);

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
            _run = await _runner.ExecuteAsync(Context(), chosen, progress, _cts.Token);
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
                var preview = await row.Step.PreviewAsync(Context(), CancellationToken.None);
                if (!preview.CanRun)
                {
                    row.Apply(StepResult.Skipped(row.Id, preview.Reason ?? preview.Summary));
                    return;
                }

                var result = await row.Step.ExecuteAsync(Context(), CancellationToken.None);
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
