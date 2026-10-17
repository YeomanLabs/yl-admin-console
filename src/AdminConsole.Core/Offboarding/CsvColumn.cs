namespace AdminConsole.Core.Offboarding;

/// <summary>The cells of the one column an imported leaver list is read from.</summary>
public sealed record CsvColumn
{
    /// <summary>Zero-based position in the file.</summary>
    public required int Index { get; init; }

    /// <summary>The column's name from the header row, or null when the file's first row was read as a person.</summary>
    public required string? Header { get; init; }

    /// <summary>Non-empty cells, trimmed, in file order.</summary>
    public required IReadOnlyList<string> Values { get; init; }

    /// <summary>Rows that had nothing in this column. They are left out, and the admin is told how many.</summary>
    public required int EmptyRows { get; init; }
}
