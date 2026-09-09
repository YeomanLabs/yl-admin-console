using CommunityToolkit.Mvvm.ComponentModel;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>Home (UX §3: the palette is the home screen). M3.5 opens the palette here; until then it explains the shortcut.</summary>
public sealed partial class HomePageViewModel : ObservableObject
{
    public string Title => "Home";

    [ObservableProperty]
    private string _hint = "Press Ctrl+K to search users, devices and groups.";
}
