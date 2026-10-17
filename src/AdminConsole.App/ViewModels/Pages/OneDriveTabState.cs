namespace AdminConsole.App.ViewModels.Pages;

/// <summary>The states of the OneDrive tab (M16.2), each with its own message.</summary>
public enum OneDriveTabState
{
    NotLoaded,
    Loading,
    Loaded,
    NeedsConsent,
    NoOneDrive,
    Failed,
}
