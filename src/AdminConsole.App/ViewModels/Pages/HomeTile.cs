using CommunityToolkit.Mvvm.Input;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One clickable figure on Home: a count and what it means, which opens the list page already filtered to it.
/// </summary>
/// <remarks>
/// The point is that a number on a dashboard is useless if seeing it is all you can do. "4 unlicensed" is a question,
/// and clicking it should put the four on screen — not leave the admin to work out which filter would have found them.
/// </remarks>
public sealed class HomeTile
{
    public HomeTile(string label, int count, string description, string navId, IReadOnlyList<string> filterKeys, Action<HomeTile> open)
    {
        Label = label;
        Count = count;
        Description = description;
        NavId = navId;
        FilterKeys = filterKeys;
        OpenCommand = new RelayCommand(() => open(this));
    }

    public string Label { get; }

    public int Count { get; }

    public string Description { get; }

    /// <summary>Nav rail id of the page this opens: users, groups or devices.</summary>
    public string NavId { get; }

    public IReadOnlyList<string> FilterKeys { get; }

    public IRelayCommand OpenCommand { get; }

    public string CountText => Count.ToString("N0");

    public string AccessibleName => $"{Label}: {Count:N0}. {Description}";

    /// <summary>Whether this is something to act on rather than just a total. Drawn in the warning colour.</summary>
    public bool IsAttention { get; init; }
}
