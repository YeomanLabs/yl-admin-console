namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One choice in a stamp row's drop-down.</summary>
public sealed record StampModeChoice(StampMode Mode, string Label)
{
    public override string ToString() => Label;
}
