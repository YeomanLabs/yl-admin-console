namespace AdminConsole.App.ViewModels.Pages;

/// <summary>A lock state in the OneDrive tab's picker.</summary>
public sealed record LockChoice(string Value, string Label)
{
    /// <summary>What a screen reader announces for the selected item, rather than the record's field dump.</summary>
    public override string ToString() => Label;
}
