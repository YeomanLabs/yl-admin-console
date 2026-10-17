namespace AdminConsole.App.ViewModels.Pages;

/// <summary>A default folder offered for folder permissions: found by type, labelled in English for the picker.</summary>
public sealed record FolderChoice(string FolderType, string Label)
{
    /// <summary>What a screen reader announces for the selected folder (the M14 picker read out the record).</summary>
    public override string ToString() => Label;
}
