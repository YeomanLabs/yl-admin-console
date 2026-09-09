namespace YeomanLabs.AdminConsole.App.ViewModels;

/// <summary>What the shell needs from any page hosted in a tab: a title for the tab strip and window title.</summary>
public interface IPageViewModel
{
    string Title { get; }
}
