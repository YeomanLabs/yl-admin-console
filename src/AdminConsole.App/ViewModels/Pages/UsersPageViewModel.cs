using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Cache.Browse;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>M9.2 — everyone in the tenant, browsable. The palette finds a person you can name; this is for the
/// questions that start "who are all the…".</summary>
public sealed partial class UsersPageViewModel : ListPageViewModel<UserRow>
{
    private readonly CacheDb _cache;

    public UsersPageViewModel(CacheDb cache, ILogger<UsersPageViewModel> logger)
        : base(HasMailboxData(cache) ? UserBrowse.Filters : UserBrowse.FiltersWithoutMailboxes, UserBrowse.Sorts, UserBrowse.DefaultSort, logger)
        => _cache = cache;

    /// <summary>Mailbox chips only once Exchange has been read: until then they would all count zero, which reads as a
    /// fact about the tenant rather than an absence of data.</summary>
    private static bool HasMailboxData(CacheDb cache) => cache.GetSyncState(CacheEntity.Mailboxes) is not null;

    public override string Title => "Users";

    public override string Subtitle => "Everyone in the directory. Click a person to open them.";

    public override string SearchPlaceholder => "Filter by name, sign-in name, job title or department — or dep=sales lic=e3 disabled=true";

    protected override string EmptyWhenNothingCached => "No users cached yet";

    protected override string EmptyHintWhenNothingCached =>
        "The first sync may still be running — the status bar at the bottom says where it is up to.";

    protected override BrowsePage<UserRow> Load(BrowseQuery query)
    {
        var page = _cache.BrowseUsers(query);
        var extras = ExtraColumns;
        HashSet<string> selected;
        lock (_selected) selected = [.. _selected.Keys];
        return new BrowsePage<UserRow>([.. page.Rows.Select(r =>
        {
            var plain = new UserRow(r);
            var extra = string.Join(" · ", extras.Select(c => (c.Label, Value: c.Value(plain))).Where(x => !string.IsNullOrEmpty(x.Value)).Select(x => $"{x.Label}: {x.Value}"));
            return new UserRow(r, extra.Length == 0 ? null : extra, selected.Contains(r.Id), OnRowSelected);
        })], page.Total, page.Skip) { Problems = page.Problems };
    }

    // ---- selection and bulk edit (M18.3) ---------------------------------------------------------------------------

    /// <summary>Ticked people by id, with the name to show. Kept apart from the rows so a filter change keeps the selection.</summary>
    private readonly Dictionary<string, string> _selected = new(StringComparer.Ordinal);

    /// <summary>Set by the shell: opens the bulk edit for these ids.</summary>
    public Func<IReadOnlyList<string>, Task>? BulkEdit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(BulkEditSelectedCommand))]
    private int _selectedCount;

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => SelectedCount == 1 ? "1 person selected" : $"{SelectedCount:N0} people selected";

    private void OnRowSelected(UserRow row, bool on)
    {
        lock (_selected)
        {
            if (on) _selected[row.Id] = row.Primary;
            else _selected.Remove(row.Id);
            SelectedCount = _selected.Count;
        }
    }

    [RelayCommand]
    private async Task SelectAllMatchingAsync()
    {
        var rows = await LoadAllMatchingAsync().ConfigureAwait(true);
        lock (_selected)
        {
            foreach (var r in rows) _selected[r.Id] = r.Primary;
            SelectedCount = _selected.Count;
        }

        foreach (var row in Rows) row.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        lock (_selected)
        {
            _selected.Clear();
            SelectedCount = 0;
        }

        foreach (var row in Rows) row.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task BulkEditSelectedAsync()
    {
        if (BulkEdit is null) return;
        List<string> ids;
        lock (_selected) ids = [.. _selected.Keys];
        await BulkEdit(ids).ConfigureAwait(true);
    }

    protected override IReadOnlyList<ListColumn<UserRow>> ColumnCatalog { get; } =
    [
        new("name", "Name", r => r.Primary),
        new("upn", "Sign-in name", r => r.Upn),
        new("title", "Job title", r => r.JobTitle),
        new("department", "Department", r => r.Department),
        new("enabled", "Enabled", r => r.Enabled ? "Yes" : "No"),
        new("licensed", "Licensed", r => r.Licensed ? "Yes" : "No"),
        new("synced", "From AD", r => r.Synced ? "Yes" : "No"),
        new("mail", "Mail", r => r.Model.Mail, DefaultOn: false, OnRow: false),
        new("type", "Type", r => r.Model.UserType, DefaultOn: false, OnRow: false),
        new("created", "Created", r => r.Model.Created?.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), DefaultOn: false, OnRow: false),
        new("id", "Object ID", r => r.Id, DefaultOn: false, OnRow: false),
    ];

    protected override IReadOnlyDictionary<string, int> CountChips(BrowseQuery query) => _cache.CountUserFilters(query);
}
