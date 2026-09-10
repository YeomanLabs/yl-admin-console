namespace AdminConsole.Core.Cache.Browse;

/// <summary>
/// What a list page is asking for: a window into one cached table, narrowed by text and by named filters.
/// M9.1 — the palette answers "I know who I want"; this answers "show me everyone and let me look".
/// </summary>
public sealed record BrowseQuery
{
    /// <summary>Free text. Narrows on the same columns the palette searches, but with a prefix match so the list
    /// filters as you type rather than waiting for a whole word.</summary>
    public string? Text { get; init; }

    /// <summary>Keys of the filters to apply, from the entity's catalog. Unknown keys are ignored rather than
    /// throwing: a saved view should survive a filter being renamed.</summary>
    public IReadOnlyList<string> FilterKeys { get; init; } = [];

    /// <summary>Sort column key from the entity's catalog. Null sorts by the entity's default.</summary>
    public string? SortKey { get; init; }

    public bool Descending { get; init; }

    public int Skip { get; init; }

    /// <summary>Rows per page. The UI pages rather than loading everything: a 50,000-user tenant should not put
    /// 50,000 rows into a WPF ItemsControl to show the first twenty.</summary>
    public int Take { get; init; } = 100;
}
