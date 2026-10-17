namespace AdminConsole.Core.Offboarding;

/// <summary>
/// A CSV file as rows of cells, before anyone has decided which column holds the people. Blank rows are already gone;
/// the first row is still whatever the file began with, a header or a person.
/// </summary>
public sealed record CsvTable
{
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    /// <summary>The delimiter the file used, or null when its first row had none (a single column).</summary>
    public required char? Delimiter { get; init; }

    /// <summary>A quote was opened and never closed, so everything after it was read as one cell.</summary>
    public bool HasUnclosedQuote { get; init; }

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}
