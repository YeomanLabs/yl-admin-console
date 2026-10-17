using System.Text;

namespace AdminConsole.Core.Export;

/// <summary>
/// CSV as Excel and every other reader expect it (M18.5): RFC 4180 quoting, UTF-8 with a byte-order mark so Excel reads
/// accents correctly, and the formula guard — a cell starting with <c>= + - @</c> or a tab is prefixed with an apostrophe,
/// so a display name like <c>=HYPERLINK(…)</c> arrives as text rather than running when the file is opened.
/// </summary>
public static class CsvExport
{
    public static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static void Write(TextWriter writer, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        writer.Write(Line(headers));
        writer.Write("\r\n");
        foreach (var row in rows)
        {
            writer.Write(Line(row));
            writer.Write("\r\n");
        }
    }

    public static string Line(IEnumerable<string?> cells) => string.Join(",", cells.Select(Cell));

    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
