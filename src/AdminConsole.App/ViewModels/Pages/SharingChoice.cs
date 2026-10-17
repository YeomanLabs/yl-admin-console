using AdminConsole.Core.OneDrive;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>A sharing level in the OneDrive tab's picker, worded as the SharePoint admin centre words it.</summary>
public sealed record SharingChoice(OneDriveSharing Value, string Label)
{
    /// <summary>What a screen reader announces for the selected item, rather than the record's field dump.</summary>
    public override string ToString() => Label;
}
