using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Cache.Browse;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Shared behaviour of the Users, Groups and Devices list pages (M9.2–9.4): a filter box, chips, a sort, a page of
/// rows, and the counts that let an admin see the shape of the tenant without opening anything.
/// </summary>
/// <remarks>
/// The three pages differ only in which catalog they use and which columns they draw, which is exactly the third use
/// CLAUDE.md asks for before abstracting. Every read runs off the UI thread: the cache is synchronous SQLite, and a
/// list page must never be the thing that makes the window stop painting.
/// </remarks>
public abstract partial class ListPageViewModel<TRow> : ObservableObject, IPageViewModel, IRefreshable
{
    /// <summary>How many rows a page holds. Big enough that most tenants never page, small enough that the first
    /// paint is instant on one that does.</summary>
    private const int PageSize = 200;

    /// <summary>Long enough to swallow a burst of typing, short enough to feel live. The palette uses 40 ms; a list
    /// re-queries and re-counts every chip, so it waits a little longer.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

    private readonly ILogger _logger;
    private CancellationTokenSource? _pending;

    protected ListPageViewModel(IReadOnlyList<BrowseFilter> filters, IReadOnlyList<BrowseSort> sorts, BrowseSort defaultSort, ILogger logger)
    {
        _logger = logger;
        Sorts = sorts;
        _selectedSort = defaultSort;
        Chips = [.. filters.Select(f => new FilterChip(f, QueueReload))];
        ChipGroups = [.. Chips.GroupBy(c => c.Filter.Group).Select(g => new FilterChipGroup(g.Key, [.. g]))];
    }

    public abstract string Title { get; }

    /// <summary>What this page is for, in one line under the heading. A client who has never seen the app should be
    /// able to read the top of the page and know what they are looking at.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Placeholder for the filter box, naming what it actually searches.</summary>
    public abstract string SearchPlaceholder { get; }

    /// <summary>Set by the shell: opens the object a row points at.</summary>
    public Action<TRow>? OpenRow { get; set; }

    /// <summary>Set by the shell: the save dialog the export asks (M18.5).</summary>
    public Services.FilePicker? Files { get; set; }

    /// <summary>Set by the shell: the status bar.</summary>
    public Action<string>? ReportStatus { get; set; }

    // ---- columns and export (M18.5) ------------------------------------------------------------------------------

    /// <summary>Every column this list can export, in order.</summary>
    protected abstract IReadOnlyList<ListColumn<TRow>> ColumnCatalog { get; }

    private ObservableCollection<ColumnChoice>? _columns;

    public ObservableCollection<ColumnChoice> Columns =>
        _columns ??= [.. ColumnCatalog.Select(c => new ColumnChoice(c.Key, c.Label, c.DefaultOn, OnColumnsChanged))];

    /// <summary>The chosen columns, in catalog order.</summary>
    protected IReadOnlyList<ListColumn<TRow>> SelectedColumns =>
        [.. ColumnCatalog.Where(c => Columns.FirstOrDefault(x => x.Key == c.Key)?.IsOn == true)];

    /// <summary>A chosen column the row does not draw itself shows as an extra line, so the screen matches the export.</summary>
    protected IReadOnlyList<ListColumn<TRow>> ExtraColumns => [.. SelectedColumns.Where(c => !c.OnRow)];

    private void OnColumnsChanged() => QueueReload();

    [ObservableProperty]
    private bool _isExporting;

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Files is null || IsExporting) return;
        var filters = Chips.Where(c => c.IsOn).Select(c => c.Label).ToList();
        if (SearchText.Trim().Length > 0) filters.Add(SearchText.Trim());
        var suggested = $"{Title}{(filters.Count == 0 ? string.Empty : " - " + string.Join(", ", filters))} {DateTime.Now:yyyy-MM-dd}.csv";
        suggested = string.Join("_", suggested.Split(System.IO.Path.GetInvalidFileNameChars()));
        if (Files.SaveAs(suggested, "CSV (*.csv)|*.csv", $"Export {Title}") is not { } path) return;

        IsExporting = true;
        try
        {
            var columns = SelectedColumns;
            var rows = await LoadAllMatchingAsync().ConfigureAwait(true);
            var count = await Task.Run(() =>
            {
                using var writer = new System.IO.StreamWriter(path, append: false, Core.Export.CsvExport.Utf8WithBom);
                Core.Export.CsvExport.Write(writer, [.. columns.Select(c => c.Label)], rows.Select(r => (IReadOnlyList<string?>)[.. columns.Select(c => c.Value(r))]));
                return rows.Count;
            });
            ReportStatus?.Invoke($"Exported {count:N0} {Title.ToLowerInvariant()} to {path}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Export of {Page} failed", Title);
            ReportStatus?.Invoke($"The export failed: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// Every row the current filters match, not just the page on screen: a page at a time from the same query the list
    /// ran, off the UI thread. What an export writes and what "select all matching" selects.
    /// </summary>
    protected Task<List<TRow>> LoadAllMatchingAsync()
    {
        var baseQuery = new BrowseQuery
        {
            Text = SearchText,
            FilterKeys = [.. Chips.Where(c => c.IsOn).Select(c => c.Key)],
            SortKey = SelectedSort.Key,
            Descending = Descending,
        };
        return Task.Run(() =>
        {
            var rows = new List<TRow>();
            for (var skip = 0; ; skip += 5000)
            {
                var page = Load(baseQuery with { Skip = skip, Take = 5000 });
                rows.AddRange(page.Rows);
                if (!page.HasMore || page.Rows.Count == 0) break;
            }

            return rows;
        });
    }

    /// <summary>What the filter box asked for that could not be used (M18.4); null when everything was understood.</summary>
    [ObservableProperty]
    private string? _queryProblem;

    // ---- what the admin has asked for ---------------------------------------------------------------------------

    [ObservableProperty]
    private string _searchText = string.Empty;

    public ObservableCollection<FilterChip> Chips { get; }

    /// <summary>The chips laid out by group, because the groups are what the OR/AND rule follows and the eye should
    /// see the same shape the query uses.</summary>
    public ObservableCollection<FilterChipGroup> ChipGroups { get; }

    public IReadOnlyList<BrowseSort> Sorts { get; }

    [ObservableProperty]
    private BrowseSort _selectedSort;

    [ObservableProperty]
    private bool _descending;

    partial void OnSearchTextChanged(string value) => QueueReload();

    partial void OnSelectedSortChanged(BrowseSort value) => QueueReload();

    partial void OnDescendingChanged(bool value) => QueueReload();

    public bool HasActiveFilters => Chips.Any(c => c.IsOn) || SearchText.Length > 0;

    [RelayCommand]
    private void ClearFilters()
    {
        foreach (var chip in Chips) chip.IsOn = false;
        SearchText = string.Empty;
    }

    [RelayCommand]
    private void ToggleSort(BrowseSort sort)
    {
        // Clicking the column you are already sorted by reverses it, the way every list in Windows behaves.
        if (SelectedSort.Key == sort.Key) Descending = !Descending;
        else { SelectedSort = sort; Descending = false; }
    }

    // ---- what came back -----------------------------------------------------------------------------------------

    public ObservableCollection<TRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _total;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private bool _hasMore;

    /// <summary>True once a query has come back at least once, so the empty state does not flash before the first
    /// read finishes.</summary>
    [ObservableProperty]
    private bool _hasLoaded;

    public string CountText => Total switch
    {
        0 => string.Empty,
        1 => "1 result",
        _ when HasMore => $"Showing {Rows.Count:N0} of {Total:N0}",
        _ => $"{Total:N0} results",
    };

    public bool IsEmpty => HasLoaded && Rows.Count == 0;

    /// <summary>Heading of the empty state. Different when a filter caused it, because the fix is different.</summary>
    public string EmptyTitle => HasActiveFilters ? "Nothing matches" : EmptyWhenNothingCached;

    public string EmptyHint => HasActiveFilters
        ? "Try removing a filter, or clear them all."
        : EmptyHintWhenNothingCached;

    /// <summary>What to say when the cache genuinely holds none of these — a different problem from an over-narrow
    /// filter, and usually means the first sync has not finished.</summary>
    protected abstract string EmptyWhenNothingCached { get; }

    protected abstract string EmptyHintWhenNothingCached { get; }

    // ---- loading ------------------------------------------------------------------------------------------------

    public Task RefreshAsync(CancellationToken ct) => ReloadAsync(append: false, ct);

    [RelayCommand]
    private Task LoadMoreAsync() => ReloadAsync(append: true, CancellationToken.None);

    /// <summary>Call once the page is on screen. Kept out of the constructor so a page never does I/O while the
    /// shell is still building it.</summary>
    public void Start() => QueueReload();

    /// <summary>
    /// Opens the page with filters already ticked — how a Home tile turns a figure into the rows behind it. The chips
    /// are set rather than the query, so the admin can see which filters produced what they are looking at and can
    /// untick them.
    /// </summary>
    public void ApplyFilters(IReadOnlyList<string> keys)
    {
        foreach (var chip in Chips) chip.IsOn = keys.Contains(chip.Key, StringComparer.Ordinal);
    }

    private void QueueReload()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));

        _pending?.Cancel();
        _pending?.Dispose();
        var cts = new CancellationTokenSource();
        _pending = cts;

        _ = DebouncedReloadAsync(cts.Token);
    }

    private async Task DebouncedReloadAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct).ConfigureAwait(true);
            await ReloadAsync(append: false, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke. Nothing to report.
        }
    }

    private async Task ReloadAsync(bool append, CancellationToken ct)
    {
        var query = new BrowseQuery
        {
            Text = SearchText,
            FilterKeys = [.. Chips.Where(c => c.IsOn).Select(c => c.Key)],
            SortKey = SelectedSort.Key,
            Descending = Descending,
            Skip = append ? Rows.Count : 0,
            Take = PageSize,
        };

        IsLoading = true;
        try
        {
            // SQLite reads are synchronous; off the UI thread they stay off it (doc 01: no UI stall over 100 ms).
            var (page, counts) = await Task.Run(() => (Load(query), CountChips(query)), ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            if (!append) Rows.Clear();
            foreach (var row in page.Rows) Rows.Add(row);

            Total = page.Total;
            HasMore = page.HasMore;
            QueryProblem = page.Problems.Count == 0 ? null : string.Join(" ", page.Problems);
            foreach (var chip in Chips) chip.Count = counts.TryGetValue(chip.Key, out var n) ? n : 0;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // A list that cannot read the cache should say so, not sit empty looking like an empty tenant.
            _logger.LogError(ex, "Browse failed on {Page}", Title);
            Rows.Clear();
            Total = 0;
        }
        finally
        {
            IsLoading = false;
            HasLoaded = true;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CountText));
        }
    }

    protected abstract BrowsePage<TRow> Load(BrowseQuery query);

    protected abstract IReadOnlyDictionary<string, int> CountChips(BrowseQuery query);

    [RelayCommand]
    private void Open(TRow row) => OpenRow?.Invoke(row);
}
