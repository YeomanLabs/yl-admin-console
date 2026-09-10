namespace AdminConsole.App.ViewModels;

/// <summary>A header badge (UX §4): text plus colour intent, optionally with a tooltip explaining it.</summary>
public sealed record BadgeItem(string Text, BadgeTone Tone = BadgeTone.Neutral, string? ToolTip = null)
{
    public bool IsGood => Tone == BadgeTone.Good;
    public bool IsWarning => Tone == BadgeTone.Warning;
    public bool IsDanger => Tone == BadgeTone.Danger;
    public bool IsInfo => Tone == BadgeTone.Info;
}
