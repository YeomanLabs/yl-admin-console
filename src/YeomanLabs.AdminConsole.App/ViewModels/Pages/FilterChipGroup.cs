using System.Collections.ObjectModel;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// The chips of one filter group, drawn together. Grouping is not decoration: chips inside a group OR and the groups
/// AND, so showing them apart is what makes ticking two boxes behave the way it looks like it should.
/// </summary>
public sealed class FilterChipGroup
{
    public FilterChipGroup(string key, IReadOnlyList<FilterChip> chips)
    {
        Key = key;
        Chips = [.. chips];
    }

    public string Key { get; }

    public ObservableCollection<FilterChip> Chips { get; }
}
