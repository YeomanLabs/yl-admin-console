using AdminConsole.Core.Models;

namespace AdminConsole.Core.Search;

/// <summary>Grouped palette results (UX §3: Users, Devices, Groups, Actions; groups collapse when empty).</summary>
public sealed record SearchResults
{
    public required ParsedQuery Query { get; init; }
    public IReadOnlyList<SearchHit> Users { get; init; } = [];
    public IReadOnlyList<SearchHit> Devices { get; init; } = [];
    public IReadOnlyList<SearchHit> Groups { get; init; } = [];
    public IReadOnlyList<SearchHit> Actions { get; init; } = [];

    /// <summary>Wall-clock time of the whole search, for the status bar and the perf gate.</summary>
    public required double ElapsedMilliseconds { get; init; }

    public bool IsEmpty => Users.Count == 0 && Devices.Count == 0 && Groups.Count == 0 && Actions.Count == 0;

    /// <summary>All hits in display order; the first one is what Enter opens.</summary>
    public IEnumerable<SearchHit> All => Users.Concat(Devices).Concat(Groups).Concat(Actions);

    /// <summary>The single best hit across groups (highest score), for Enter on the top result.</summary>
    public SearchHit? Top => All.OrderByDescending(h => h.Score).FirstOrDefault();
}
