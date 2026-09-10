using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.App.ViewModels;

/// <summary>
/// One tab of the content region (UX §2, §11: Ctrl+T/W/Tab). Owns a back stack (Alt+←) of page ViewModels; pages that
/// implement <see cref="IDisposable"/> are disposed when they leave the stack.
/// </summary>
public sealed partial class WorkspaceTab : ObservableObject
{
    private readonly Stack<(object Page, NavItem? Nav)> _back = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private object? _currentPage;

    /// <summary>The rail item that produced the current page, so switching tabs or going back re-highlights the rail.</summary>
    [ObservableProperty]
    private NavItem? _nav;

    [ObservableProperty]
    private bool _isSelected;

    public string Title => (CurrentPage as IPageViewModel)?.Title ?? "New tab";

    public bool CanGoBack => _back.Count > 0;

    public void Navigate(object page, NavItem? nav)
    {
        if (CurrentPage is not null) _back.Push((CurrentPage, Nav));
        CurrentPage = page;
        Nav = nav;
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Alt+←. Returns false when there is nothing to go back to.</summary>
    public bool GoBack()
    {
        if (_back.Count == 0) return false;
        (CurrentPage as IDisposable)?.Dispose();
        var (page, nav) = _back.Pop();
        CurrentPage = page;
        Nav = nav;
        OnPropertyChanged(nameof(CanGoBack));
        return true;
    }

    public void DisposeAll()
    {
        (CurrentPage as IDisposable)?.Dispose();
        foreach (var (page, _) in _back) (page as IDisposable)?.Dispose();
        _back.Clear();
        CurrentPage = null;
        Nav = null;
    }
}
