using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One section of the Mailbox tab (M13.5). Loads and fails on its own, like User 360's Graph sections: a 403 on Send As
/// must not blank the mailbox size. Never shows nothing without saying why (M9.7) — loading, an error, an empty-state
/// sentence, or rows.
/// </summary>
public sealed partial class MailboxSection : ObservableObject
{
    public MailboxSection(string title, string emptyText)
    {
        Title = title;
        EmptyText = emptyText;
    }

    public string Title { get; }

    /// <summary>What an empty section means, in words — "Nobody else has Full Access", not a blank.</summary>
    public string EmptyText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private IReadOnlyList<FactRow> _facts = [];

    /// <summary>A line under the rows, for what applies to the whole section.</summary>
    [ObservableProperty]
    private string? _note;

    public bool ShowEmpty => !IsLoading && Error is null && Facts.Count == 0;

    public void Show(IReadOnlyList<FactRow> facts, string? note = null)
    {
        Facts = facts;
        Note = note;
        Error = null;
        IsLoading = false;
    }

    /// <summary>The section's name in the automation tree, where an items control names each item by its ToString.</summary>
    public override string ToString() => Title;

    public void Fail(string message)
    {
        Facts = [];
        Error = message;
        IsLoading = false;
    }
}
