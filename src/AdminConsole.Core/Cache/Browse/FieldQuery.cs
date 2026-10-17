namespace AdminConsole.Core.Cache.Browse;

/// <summary>
/// A filter box's text split into the <c>key=value</c> parts it recognises and the free text left over (M18.4). An
/// unknown key or a value a yes/no field cannot read is reported, never silently dropped: a query that quietly ignored
/// half of itself would show the wrong people with no sign anything was wrong.
/// </summary>
public sealed record FieldQuery(string FreeText, IReadOnlyList<(string Sql, string? Value, bool Whole)> Conditions, IReadOnlyList<string> Problems)
{
    private static readonly string[] Yes = ["true", "yes", "1", "on"];
    private static readonly string[] No = ["false", "no", "0", "off"];

    public static FieldQuery Parse(string? text, IReadOnlyList<BrowseField> fields)
    {
        var free = new List<string>();
        var conditions = new List<(string, string?, bool)>();
        var problems = new List<string>();

        foreach (var token in Tokens(text ?? string.Empty))
        {
            var eq = token.IndexOf('=');
            var key = eq > 0 ? token[..eq] : null;
            if (key is null || !key.All(char.IsLetter))
            {
                free.Add(token);
                continue;
            }

            var value = token[(eq + 1)..].Trim('"');
            var field = fields.FirstOrDefault(f => f.Matches(key));
            if (field is null)
            {
                problems.Add($"\"{key}\" isn't a field here. Try {string.Join(", ", fields.Select(f => f.Key))}.");
                continue;
            }

            if (field.IsBoolean)
            {
                if (Yes.Contains(value, StringComparer.OrdinalIgnoreCase)) conditions.Add((field.TrueSql!, null, false));
                else if (No.Contains(value, StringComparer.OrdinalIgnoreCase)) conditions.Add((field.FalseSql!, null, false));
                else problems.Add($"{field.Key} takes true or false, not \"{value}\".");
                continue;
            }

            if (value.Length == 0)
            {
                problems.Add($"{field.Key}= needs a value.");
                continue;
            }

            conditions.Add((field.TextSql!, value, field.Exact));
        }

        return new FieldQuery(string.Join(' ', free), conditions, problems);
    }

    /// <summary>Whitespace-separated, with "double quotes" keeping a value's spaces: <c>dep="field services"</c>.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) yield return current.ToString();
    }
}
