using System.Globalization;
using System.Text;

namespace YeomanLabs.AdminConsole.Core.Templates;

/// <summary>
/// Expands the naming rules of doc 07 — <c>{first}.{last}@{domain}</c> and friends. Pure and side-effect free, because
/// getting a UPN wrong is the one mistake in a create flow that cannot be quietly corrected afterwards.
/// </summary>
public static class NameRules
{
    /// <summary>
    /// Builds a display name. Not sanitised: a person called Zoë should be shown as Zoë, and Graph accepts it.
    /// </summary>
    public static string ExpandDisplay(string rule, string first, string last) =>
        Expand(rule, first, last, domain: null, sanitise: false);

    /// <summary>
    /// Builds a UPN or a mail nickname. Sanitised: accents are folded to ASCII and anything Entra would reject in a
    /// local part is dropped, so "Zoë O'Brien" becomes "zoe.obrien" rather than a UPN Graph refuses.
    /// </summary>
    public static string ExpandAddress(string rule, string first, string last, string? domain = null) =>
        Expand(rule, first, last, domain, sanitise: true);

    private static string Expand(string rule, string first, string last, string? domain, bool sanitise)
    {
        first = first.Trim();
        last = last.Trim();

        var sb = new StringBuilder(rule.Length + first.Length + last.Length + 16);
        for (var i = 0; i < rule.Length; i++)
        {
            if (rule[i] != '{')
            {
                sb.Append(rule[i]);
                continue;
            }

            var close = rule.IndexOf('}', i + 1);
            if (close < 0)
            {
                // An unclosed brace is a typo in the rule, not a token. Leave it visible rather than swallowing it.
                sb.Append(rule[i..]);
                break;
            }

            var token = rule[(i + 1)..close];
            sb.Append(Replace(token, first, last, domain));
            i = close;
        }

        var text = sb.ToString();
        return sanitise ? SanitiseAddress(text) : text.Trim();
    }

    /// <summary>
    /// Doc 07 lists <c>{first}</c>, <c>{last}</c>, <c>{First}</c>, <c>{Last}</c> and <c>{domain}</c>. Initials are
    /// added because <c>{f}{last}</c> is how a great many organisations build a UPN, and the alternative is an admin
    /// hand-editing every one.
    /// </summary>
    private static string Replace(string token, string first, string last, string? domain) => token switch
    {
        "first" => first.ToLowerInvariant(),
        "last" => last.ToLowerInvariant(),
        "First" => TitleCase(first),
        "Last" => TitleCase(last),
        "f" => Initial(first).ToLowerInvariant(),
        "l" => Initial(last).ToLowerInvariant(),
        "F" => Initial(first).ToUpperInvariant(),
        "L" => Initial(last).ToUpperInvariant(),
        "domain" => domain ?? string.Empty,

        // An unknown token is left as written so the admin sees their typo instead of a silently empty UPN.
        _ => $"{{{token}}}",
    };

    private static string Initial(string value) => value.Length == 0 ? string.Empty : value[..1];

    private static string TitleCase(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>
    /// Folds accents to ASCII and keeps only what Entra accepts in a UPN local part or a mail nickname: letters,
    /// digits, and <c>. - _</c>. Leading, trailing and doubled separators are collapsed.
    /// </summary>
    public static string SanitiseAddress(string value)
    {
        var at = value.IndexOf('@');
        var local = at < 0 ? value : value[..at];
        var domain = at < 0 ? null : value[(at + 1)..];

        local = Clean(local);
        if (domain is null) return local;

        // The domain comes from the tenant's verified list, so it is left alone apart from trimming.
        return $"{local}@{domain.Trim().ToLowerInvariant()}";
    }

    private static string Clean(string value)
    {
        var folded = Fold(value);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(char.ToLowerInvariant(c));
            else if (c is '.' or '-' or '_') sb.Append(c);
        }

        var text = sb.ToString();
        while (text.Contains("..", StringComparison.Ordinal)) text = text.Replace("..", ".", StringComparison.Ordinal);
        return text.Trim('.', '-', '_');
    }

    /// <summary>"Zoë" → "Zoe". Decomposes, then drops the combining marks.</summary>
    private static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
