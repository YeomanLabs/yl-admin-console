namespace AdminConsole.Core.Search;

/// <summary>The palette input after prefix and normalization (UX §3). See <see cref="SearchService.Parse"/>.</summary>
public sealed record ParsedQuery
{
    public required string Original { get; init; }

    /// <summary>Scope from the prefix, or the caller's scope when no prefix was typed.</summary>
    public required SearchScope Scope { get; init; }

    /// <summary>Trimmed, lower-cased text with the prefix removed.</summary>
    public required string Text { get; init; }

    /// <summary>Alphanumeric tokens of <see cref="Text"/>, matching how FTS5 unicode61 tokenizes stored values.</summary>
    public required IReadOnlyList<string> Terms { get; init; }

    /// <summary>Digits of <see cref="Text"/> when it looks like a phone/ID fragment (≥ 3 digits, nothing else but separators).</summary>
    public string? Digits { get; init; }

    public bool IsEmpty => Terms.Count == 0 && Digits is null;
}
