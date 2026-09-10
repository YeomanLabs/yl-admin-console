namespace AdminConsole.Core.Models;

/// <summary>One palette row (doc 05): icon by <see cref="ObjectType"/>, primary text, secondary text, right-aligned badges.</summary>
public sealed record SearchHit
{
    public required ObjectType ObjectType { get; init; }
    public required string Id { get; init; }
    public required string Primary { get; init; }
    public string? Secondary { get; init; }
    public IReadOnlyList<string> Badges { get; init; } = [];

    /// <summary>Higher is better. Tier (prefix/word/substring/fts) plus recency boost minus bm25 tiebreak.</summary>
    public required double Score { get; init; }
}
