using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One section of the Mailbox tab (M13.5, editable since M14). Loads and fails on its own, like User 360's Graph sections:
/// a 403 on Send As must not blank the mailbox size. Never shows nothing without saying why (M9.7) — loading, an error,
/// an empty-state sentence, or rows.
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
    private IReadOnlyList<MailboxRow> _facts = [];

    /// <summary>A line under the rows, for what applies to the whole section.</summary>
    [ObservableProperty]
    private string? _note;

    /// <summary>Buttons under the rows. Empty when the section is read-only or the admin cannot change it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActions))]
    private IReadOnlyList<SectionAction> _actions = [];

    /// <summary>Why the section cannot be changed, in place of its buttons (UX §6) — e.g. mastered in AD.</summary>
    [ObservableProperty]
    private string? _blockedReason;

    /// <summary>A setting that shapes the section's next action rather than the mailbox — "map new delegates into Outlook".</summary>
    public string? ToggleLabel { get; init; }

    [ObservableProperty]
    private bool _toggleValue;

    public bool HasToggle => ToggleLabel is not null;

    public bool HasActions => Actions.Count > 0;

    public bool ShowEmpty => !IsLoading && Error is null && Facts.Count == 0;

    public void Show(IReadOnlyList<MailboxRow> facts, string? note = null)
    {
        Facts = facts;
        Note = note;
        Error = null;
        IsLoading = false;
    }

    public void Fail(string message)
    {
        Facts = [];
        Actions = [];
        Error = message;
        IsLoading = false;
    }

    /// <summary>The section's name in the automation tree, where an items control names each item by its ToString.</summary>
    public override string ToString() => Title;
}
