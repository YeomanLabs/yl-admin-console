using System.Text;

namespace AdminConsole.Core.Offboarding;

/// <summary>
/// A leaver list as a CSV file — what HR sends, or the console's own export filtered in Excel. Read the way Excel writes
/// it in any locale: RFC 4180 quoting, with or without a UTF-8 byte-order mark, comma or semicolon (or tab) between
/// cells, and Excel's legacy encoding when the file is not UTF-8. The people column is chosen by its name; the values
/// then go through <see cref="OffboardList"/> like a pasted list, so the exact-match rule is the same.
/// </summary>
public static class LeaverCsv
{
    /// <summary>A leaver list is a few kilobytes. Anything this large is the wrong file.</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Column names that hold a person, best first: a sign-in name identifies exactly one account, a display name may
    /// not. Matched whole, ignoring case, spaces and punctuation ("User principal name", "E-mail", "Sign-in name"), so
    /// a "Manager email" column is never taken for the leaver's.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownColumns =
        ["UserPrincipalName", "UPN", "SignInName", "Email", "EmailAddress", "Mail", "PrimarySmtpAddress", "DisplayName", "Name"];

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads the file while Excel still has it open (Excel holds a write lock, so a plain read is refused), and decodes it.
    /// </summary>
    public static string ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxFileBytes)
        {
            throw new InvalidDataException(
                $"It is {stream.Length / (1024.0 * 1024.0):0.#} MB; a leaver list is a few kilobytes. Check this is the right file.");
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return Decode(bytes);
    }

    /// <summary>
    /// UTF-8 or UTF-16 when the file says so with a byte-order mark; UTF-8 when the bytes are valid UTF-8; otherwise the
    /// Windows ANSI code page, which is what Excel's plain "CSV" format writes — so "Müller" still matches.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes, Encoding? ansi = null)
    {
        if (bytes.StartsWith(Utf8Bom)) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith(Utf16LeBom)) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith(Utf16BeBom)) return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Code page 0 is the system's ANSI code page on Windows.
            return (ansi ?? CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.Latin1).GetString(bytes);
        }
    }

    /// <summary>RFC 4180, leniently: a quote mid-cell is kept as text, and blank rows are dropped.</summary>
    public static CsvTable Parse(string text)
    {
        if (text.StartsWith('﻿')) text = text[1..];

        // Excel writes and honours a first line of "sep=;" to say which delimiter the file uses.
        char? delimiter;
        if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && text.Length > 4 && text[4] is not ('\r' or '\n'))
        {
            delimiter = text[4];
            var end = text.IndexOfAny(['\r', '\n']);
            text = end < 0 ? string.Empty : text[end..].TrimStart('\r', '\n');
        }
        else
        {
            delimiter = DetectDelimiter(text);
        }

        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var cellStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') cell.Append(text[++i]);
                else quoted = false;
                continue;
            }

            if (c == '"' && cellStart)
            {
                quoted = true;
                cellStart = false;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
                cellStart = true;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                cellStart = true;
                AddRow(rows, row);
                row = [];
            }
            else
            {
                cell.Append(c);
                cellStart = false;
            }
        }

        if (!cellStart || row.Count > 0)
        {
            row.Add(cell.ToString());
            AddRow(rows, row);
        }

        return new CsvTable { Rows = rows, Delimiter = delimiter, HasUnclosedQuote = quoted };
    }

    /// <summary>The best-named people column in a header row, or null when no column has a name in <see cref="KnownColumns"/>.</summary>
    public static int? FindColumn(IReadOnlyList<string> header)
    {
        var names = header.Select(Normalise).ToList();
        foreach (var known in KnownColumns)
        {
            var index = names.IndexOf(Normalise(known));
            if (index >= 0) return index;
        }

        return null;
    }

    /// <summary>The values in one column, skipping the header row when there is one.</summary>
    public static CsvColumn Take(CsvTable table, int index, bool hasHeader)
    {
        var values = new List<string>();
        var empty = 0;
        foreach (var row in table.Rows.Skip(hasHeader ? 1 : 0))
        {
            var value = index < row.Count ? row[index].Trim() : string.Empty;
            if (value.Length == 0) empty++;
            else values.Add(value);
        }

        return new CsvColumn
        {
            Index = index,
            Header = hasHeader && table.Rows.Count > 0 && index < table.Rows[0].Count ? table.Rows[0][index].Trim() : null,
            Values = values,
            EmptyRows = empty,
        };
    }

    /// <summary>
    /// Counted on the first row that is not blank, outside quotes. A tie goes to the semicolon: where Excel uses
    /// semicolons it leaves a comma unquoted, so "Vance, Adele;adele@contoso.com" is one name and one address.
    /// </summary>
    private static char? DetectDelimiter(string text)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        var quoted = false;
        var started = false;
        foreach (var c in text)
        {
            if (c is ('\r' or '\n') && !quoted)
            {
                if (started) break;
                continue;
            }

            started |= !char.IsWhiteSpace(c);
            if (c == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (c == ',') commas++;
            else if (c == ';') semicolons++;
            else if (c == '\t') tabs++;
        }

        if (tabs > commas && tabs > semicolons) return '\t';
        if (semicolons > 0 && semicolons >= commas) return ';';
        return commas > 0 ? ',' : null;
    }

    private static void AddRow(List<IReadOnlyList<string>> rows, List<string> row)
    {
        if (row.Any(c => !string.IsNullOrWhiteSpace(c))) rows.Add(row);
    }

    private static string Normalise(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
