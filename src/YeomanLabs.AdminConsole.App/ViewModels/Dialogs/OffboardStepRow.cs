using CommunityToolkit.Mvvm.ComponentModel;
using YeomanLabs.AdminConsole.Core.Offboarding;

namespace YeomanLabs.AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// One row of the offboard checklist (UX §8). The same row is the toggle in the preview and the log line during the
/// run, so the admin's eye never has to move to a different list to see what happened to the thing they ticked.
/// </summary>
public sealed partial class OffboardStepRow : ObservableObject
{
    public OffboardStepRow(IOffboardingStep step, bool enabled)
    {
        Step = step;
        _isEnabled = enabled;
    }

    public IOffboardingStep Step { get; }
    public string Id => Step.Id;
    public string Title => Step.Title;

    /// <summary>Which system this step talks to; the row shows it when it is not Graph.</summary>
    public string? SurfaceLabel => Step.Surface switch
    {
        StepSurface.Exchange => "Exchange",
        StepSurface.OnPremAd => "On-prem AD",
        _ => null,
    };

    public bool HasSurfaceLabel => SurfaceLabel is not null;

    // ---- preview ----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summary;

    public bool HasSummary => Summary is not null;

    [ObservableProperty]
    private IReadOnlyList<string> _details = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReason))]
    private string? _reason;

    public bool HasReason => Reason is not null;

    /// <summary>False greys the row: the preview said it cannot run, or the run has started.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isBlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isLocked;

    public bool CanToggle => !IsBlocked && !IsLocked;

    [ObservableProperty]
    private bool _isPreviewing = true;

    // ---- run --------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(HasRun))]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    private StepOutcome? _outcome;

    public bool IsRunning => Outcome == StepOutcome.Running;
    public bool HasRun => Outcome is not null and not StepOutcome.Running;

    /// <summary>UX §8: a tick, a cross, or the skipped mark. Empty while the step has not run.</summary>
    public string Glyph => Outcome switch
    {
        StepOutcome.Success => "✓",
        StepOutcome.Failed => "✗",
        StepOutcome.Skipped => "⊘",
        StepOutcome.NotRun => "–",
        _ => string.Empty,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultMessage))]
    private string? _resultMessage;

    public bool HasResultMessage => ResultMessage is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuration))]
    private string? _durationText;

    public bool HasDuration => DurationText is not null;

    /// <summary>Raw Graph error behind the Details expander (UX §7).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetails))]
    private string? _errorDetails;

    public bool HasErrorDetails => ErrorDetails is not null;

    [ObservableProperty]
    private bool _isRetrying;

    public bool CanRetry => Outcome == StepOutcome.Failed && !IsRetrying;

    public void Apply(StepPreview preview)
    {
        IsPreviewing = false;
        Summary = preview.Summary;
        Details = preview.Details;
        Reason = preview.Reason;
        IsBlocked = !preview.CanRun;
        if (IsBlocked) IsEnabled = false;
    }

    public void Apply(StepProgress progress)
    {
        Outcome = progress.Outcome;
        if (progress.Message is not null) ResultMessage = progress.Message;
        DurationText = progress.Duration > TimeSpan.Zero ? $"{progress.Duration.TotalSeconds:F1} s" : null;
    }

    public void Apply(StepResult result)
    {
        Outcome = result.Outcome;
        ResultMessage = result.Message;
        DurationText = result.Duration > TimeSpan.Zero ? $"{result.Duration.TotalSeconds:F1} s" : null;
        ErrorDetails = result.Details;
    }
}
