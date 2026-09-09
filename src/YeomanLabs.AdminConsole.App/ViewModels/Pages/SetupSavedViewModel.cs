namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Shown after the registration is saved (M10.2). The client ID is read when the auth service is built, so it takes a
/// restart to use it — and being told that is better than an app that appears to do nothing, or one that restarts
/// itself and looks like it crashed.
/// </summary>
public sealed class SetupSavedViewModel : IPageViewModel
{
    public string Title => "Setup";
}
