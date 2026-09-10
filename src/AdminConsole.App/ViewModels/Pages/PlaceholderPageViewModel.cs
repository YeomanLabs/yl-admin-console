using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>Stands in for nav pages that arrive in later milestones (Users/Groups/Devices lists, Templates, Activity, Settings).</summary>
public sealed partial class PlaceholderPageViewModel : ObservableObject, IPageViewModel
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _arrivesIn = string.Empty;
}
