using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace YeomanLabs.AdminConsole.App.ViewModels;

/// <summary>One entry of the 48 px nav rail (UX §2). <see cref="Ordinal"/> is the Alt+N shortcut (UX §11).</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(string id, string title, SymbolRegular symbol, int ordinal, Type pageViewModelType)
    {
        Id = id;
        Title = title;
        Symbol = symbol;
        Ordinal = ordinal;
        PageViewModelType = pageViewModelType;
    }

    public string Id { get; }
    public string Title { get; }
    public SymbolRegular Symbol { get; }
    public int Ordinal { get; }
    public Type PageViewModelType { get; }

    public string ToolTip => Ordinal > 0 ? $"{Title} (Alt+{Ordinal})" : Title;

    [ObservableProperty]
    private bool _isSelected;
}
