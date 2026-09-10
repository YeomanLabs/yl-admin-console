using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Cache.Browse;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One filter chip on a list page. Carries the count of what ticking it would show, so the admin can see a filter is
/// worth ticking before ticking it — UX §6: never make them click to find out something is empty.
/// </summary>
public sealed partial class FilterChip : ObservableObject
{
    public FilterChip(BrowseFilter filter, Action onToggled)
    {
        Filter = filter;
        _onToggled = onToggled;
    }

    private readonly Action _onToggled;

    public BrowseFilter Filter { get; }

    public string Key => Filter.Key;

    public string Label => Filter.Label;

    /// <summary>Tooltip. Empty for the self-evident ones (an OS name explains itself).</summary>
    public string Description => Filter.Description;

    public bool HasDescription => Description.Length > 0;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    public string CountText => Count.ToString("N0");

    /// <summary>What a screen reader announces. The count is spoken with the label, because "Disabled" alone does not
    /// say whether ticking it is worth doing.</summary>
    public string AccessibleName => Count == 0 ? $"{Label}, none matching" : $"{Label}, {Count:N0} matching";

    /// <summary>Nothing would match. The chip stays clickable — it is information, not a broken control — but it is
    /// dimmed so the eye skips it.</summary>
    public bool IsEmpty => Count == 0;

    partial void OnIsOnChanged(bool value) => _onToggled();
}
