using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;
using AdminConsole.Core.Models;

namespace AdminConsole.App.ViewModels.Palette;

/// <summary>One palette result row (UX §3: icon · primary · secondary · badges) with keyboard selection state.</summary>
public sealed partial class PaletteRow : ObservableObject
{
    public PaletteRow(SearchHit hit) => Hit = hit;

    public SearchHit Hit { get; }

    public string Primary => Hit.Primary;
    public string? Secondary => Hit.Secondary;
    public IReadOnlyList<string> Badges => Hit.Badges;
    public bool HasSecondary => !string.IsNullOrEmpty(Hit.Secondary);

    public SymbolRegular Symbol => Hit.ObjectType switch
    {
        ObjectType.User => SymbolRegular.Person24,
        ObjectType.Device => SymbolRegular.Laptop24,
        ObjectType.Group => SymbolRegular.PeopleTeam24,
        _ => SymbolRegular.ChevronRight24,
    };

    [ObservableProperty]
    private bool _isSelected;
}
