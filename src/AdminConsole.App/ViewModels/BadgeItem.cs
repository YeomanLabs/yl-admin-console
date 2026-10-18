namespace AdminConsole.App.ViewModels;

/// <summary>A header badge (UX §4): text plus colour intent, optionally with a tooltip explaining it.</summary>
public sealed record BadgeItem(string Text, BadgeTone Tone = BadgeTone.Neutral, string? ToolTip = null)
{
    public bool IsGood => Tone == BadgeTone.Good;
    public bool IsWarning => Tone == BadgeTone.Warning;
    public bool IsDanger => Tone == BadgeTone.Danger;
    public bool IsInfo => Tone == BadgeTone.Info;

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so a refilled list whose
    // rows equal the ones before reused their detached peers, and a screen reader lost the rows (0.3.3).
    public bool Equals(BadgeItem? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
