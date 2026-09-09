using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Search;
using YeomanLabs.AdminConsole.Core.Sync;

namespace YeomanLabs.AdminConsole.App.ViewModels.Palette;

/// <summary>
/// The command palette (UX §3): 40 ms debounce, grouped results (max 8 per group), keyboard navigation, prefixes,
/// empty and partial-index states. Search hits the local cache only; every query's timing is logged for the M3 gate.
/// </summary>
public sealed partial class PaletteViewModel : ObservableObject
{
    private const int DebounceMs = 40;
    private const int PerGroup = 8;

    private readonly SearchService _search;
    private readonly CacheDb _db;
    private readonly ISyncStatus _sync;
    private readonly ILogger<PaletteViewModel> _logger;
    private readonly List<PaletteRow> _flat = [];
    private CancellationTokenSource? _pending;

    public PaletteViewModel(SearchService search, CacheDb db, ISyncStatus sync, ILogger<PaletteViewModel> logger)
    {
        _search = search;
        _db = db;
        _sync = sync;
        _logger = logger;
    }

    /// <summary>Raised on Enter / Ctrl+Enter / click. The shell opens the object or runs the action.</summary>
    public event EventHandler<PaletteChoice>? Chosen;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = string.Empty;

    public ObservableCollection<PaletteGroup> Groups { get; } = [];

    [ObservableProperty]
    private PaletteRow? _selected;

    /// <summary>Empty-state or result-summary text under the list.</summary>
    [ObservableProperty]
    private string _footerText = string.Empty;

    /// <summary>"Building local index… 62% · results may be incomplete" while the initial walk is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartialWarning))]
    private string? _partialIndexText;

    public bool HasPartialWarning => PartialIndexText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private int _hitCount;

    public bool HasResults => HitCount > 0;

    public void Open()
    {
        Query = string.Empty;
        ClearResults();
        RefreshEmptyState();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        _pending?.Cancel();
        IsOpen = false;
    }

    partial void OnQueryChanged(string value) => _ = DebouncedSearchAsync(value);

    private async Task DebouncedSearchAsync(string query)
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        try
        {
            await Task.Delay(DebounceMs, cts.Token);
            if (string.IsNullOrWhiteSpace(query) && !query.TrimStart().StartsWith('>'))
            {
                ClearResults();
                RefreshEmptyState();
                return;
            }

            var results = await _search.SearchAsync(query, SearchScope.All, PerGroup, cts.Token);
            if (cts.IsCancellationRequested) return;
            Apply(results);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Palette search failed");
            FooterText = "Search failed: " + ex.Message;
        }
    }

    private void Apply(SearchResults r)
    {
        Groups.Clear();
        _flat.Clear();
        AddGroup("Users", r.Users);
        AddGroup("Devices", r.Devices);
        AddGroup("Groups", r.Groups);
        AddGroup("Actions", r.Actions);
        HitCount = _flat.Count;

        var top = r.Top;
        Select(top is null ? null : _flat.FirstOrDefault(row => ReferenceEquals(row.Hit, top)) ?? _flat.FirstOrDefault());

        FooterText = HitCount == 0
            ? $"No matches for \"{r.Query.Text}\"" + (r.Query.Scope == SearchScope.All ? string.Empty : $" in {r.Query.Scope.ToString().ToLowerInvariant()}")
            : $"{HitCount} result{(HitCount == 1 ? string.Empty : "s")} · {r.ElapsedMilliseconds:F1} ms";
        RefreshPartialWarning();
        _logger.LogDebug("Palette \"{Query}\" → {Count} hit(s) in {Ms:F1} ms", r.Query.Text, HitCount, r.ElapsedMilliseconds);
    }

    private void AddGroup(string title, IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count == 0) return;
        var rows = hits.Select(h => new PaletteRow(h)).ToList();
        _flat.AddRange(rows);
        Groups.Add(new PaletteGroup(title, rows));
    }

    private void ClearResults()
    {
        Groups.Clear();
        _flat.Clear();
        HitCount = 0;
        Select(null);
    }

    private void Select(PaletteRow? row)
    {
        if (Selected is not null) Selected.IsSelected = false;
        Selected = row;
        if (row is not null) row.IsSelected = true;
    }

    [RelayCommand]
    private void MoveDown() => Move(+1);

    [RelayCommand]
    private void MoveUp() => Move(-1);

    private void Move(int delta)
    {
        if (_flat.Count == 0) return;
        var i = Selected is null ? -1 : _flat.IndexOf(Selected);
        i = i < 0 ? (delta > 0 ? 0 : _flat.Count - 1) : (i + delta + _flat.Count) % _flat.Count;
        Select(_flat[i]);
    }

    /// <summary>Enter.</summary>
    [RelayCommand]
    private void OpenSelected() => Choose(Selected, newTab: false);

    /// <summary>Ctrl+Enter.</summary>
    [RelayCommand]
    private void OpenSelectedInNewTab() => Choose(Selected, newTab: true);

    /// <summary>Mouse click on a row.</summary>
    [RelayCommand]
    private void ChooseRow(PaletteRow row) => Choose(row, newTab: false);

    private void Choose(PaletteRow? row, bool newTab)
    {
        if (row is null) return;
        Close();
        Chosen?.Invoke(this, new PaletteChoice(row.Hit, newTab));
    }

    /// <summary>UX §3: "Type to search 4,812 users, 2,104 devices, 943 groups · last synced 2 min ago".</summary>
    private void RefreshEmptyState()
    {
        var states = _db.GetAllSyncState().ToDictionary(s => s.Entity);
        int Count(CacheEntity e) => states.TryGetValue(e, out var s) ? s.RowCount : 0;
        var last = states.Values.Select(s => s.LastSync).Max();
        FooterText = $"Type to search {Count(CacheEntity.Users):N0} users, {Count(CacheEntity.ManagedDevices):N0} devices, {Count(CacheEntity.Groups):N0} groups · last synced {Age(last)}";
        RefreshPartialWarning();
    }

    private void RefreshPartialWarning()
    {
        if (_sync.IsInitialSyncComplete)
        {
            PartialIndexText = null;
            return;
        }

        var pct = _sync.Latest.TryGetValue(CacheEntity.Users, out var p) && p.Fraction is { } f ? $" {f * 100:F0}%" : string.Empty;
        PartialIndexText = $"Building local index…{pct} · results may be incomplete";
    }

    private static string Age(DateTimeOffset? at)
    {
        if (at is null) return "never";
        var age = DateTimeOffset.UtcNow - at.Value;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
    }
}
