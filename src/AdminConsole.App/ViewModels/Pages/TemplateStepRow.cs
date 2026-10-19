using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Steps;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One step's tick in an offboarding template. The snapshot cannot be unticked (Restore depends on it) and a step that
/// is not built cannot be ticked; both say why, as the offboard dialog does.
/// </summary>
public sealed partial class TemplateStepRow : ObservableObject
{
    private readonly Action _changed;

    public TemplateStepRow(IOffboardingStep step, bool isChecked, Action changed)
    {
        Step = step;
        _changed = changed;
        _isChecked = step.IsRequired || (isChecked && step is not NotInThisPhaseStep);
    }

    public IOffboardingStep Step { get; }
    public string Id => Step.Id;
    public string Title => Step.Title;

    /// <summary>Fixed either way: always on (the snapshot) or never available (not built).</summary>
    public bool IsLocked => Step.IsRequired || Step is NotInThisPhaseStep;

    public bool CanToggle => !IsLocked;

    public string? Note => Step switch
    {
        { IsRequired: true } => "Always runs: it is what Restore puts the account back from.",
        NotInThisPhaseStep missing => missing.Reason,
        _ => null,
    };

    public bool HasNote => Note is not null;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _changed();

    // Reference identity: a refilled list must not hand a new row an old row's automation peer (0.3.3).
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => IsChecked ? $"{Title}, ticked" : $"{Title}, not ticked";
}
