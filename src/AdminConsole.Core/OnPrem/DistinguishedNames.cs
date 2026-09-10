using System.Text;

namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Reading and writing distinguished names (M12.1).
/// </summary>
/// <remarks>
/// Splitting on commas is wrong and it is the mistake everybody makes: <c>CN=Smith\, John,OU=Sales,DC=corp</c> is one
/// person, not two components. Every DN the console shows, parents, or builds goes through here.
/// </remarks>
public static class DistinguishedNames
{
    /// <summary>The parent container of a DN, or null for a naming context root.</summary>
    public static string? Parent(string? dn)
    {
        var parts = Split(dn);
        return parts.Count <= 1 ? null : string.Join(",", parts.Skip(1));
    }

    /// <summary>The leaf value — the CN or OU of the object itself, unescaped.</summary>
    public static string? LeafName(string? dn)
    {
        var first = Split(dn).FirstOrDefault();
        if (first is null) return null;
        var eq = first.IndexOf('=');
        return eq < 0 ? Unescape(first) : Unescape(first[(eq + 1)..]);
    }

    /// <summary>
    /// Splits a DN into its components, respecting escaped commas and quoted values. Returns an empty list for
    /// null or whitespace rather than throwing: a DN is often absent, and that is not an error.
    /// </summary>
    public static IReadOnlyList<string> Split(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return [];

        var parts = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        var quoted = false;

        foreach (var c in dn)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            switch (c)
            {
                case '\\':
                    current.Append(c);
                    escaped = true;
                    break;
                case '"':
                    quoted = !quoted;
                    current.Append(c);
                    break;
                case ',' when !quoted:
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.Length > 0) parts.Add(current.ToString().Trim());
        return [.. parts.Where(p => p.Length > 0)];
    }

    /// <summary>
    /// A DN in the form a person reads: "Sales / Users / Lab" rather than the whole string backwards. Domain
    /// components are dropped — everything on screen is in the same domain, so repeating it adds nothing.
    /// </summary>
    public static string Friendly(string? dn)
    {
        var containers = Split(dn)
            .Skip(1)
            .Where(p => !p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => Unescape(p[(p.IndexOf('=') + 1)..]))
            .Reverse()
            .ToList();

        return containers.Count == 0 ? string.Empty : string.Join(" / ", containers);
    }

    /// <summary>
    /// The readable path of a container itself, leaf included: <c>OU=Disabled,OU=Lab,DC=corp</c> becomes
    /// <c>Lab / Disabled</c> (M12.6).
    /// </summary>
    /// <remarks>
    /// <see cref="Friendly"/> answers "where does this object live" and so drops the leaf, which is right for a user
    /// and wrong for an OU — naming a move target with it produced "moving to Lab" when the target was Lab / Disabled.
    /// </remarks>
    public static string FriendlyPath(string? dn)
    {
        var containers = Split(dn)
            .Where(p => !p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => Unescape(p[(p.IndexOf('=') + 1)..]))
            .Reverse()
            .ToList();

        return containers.Count == 0 ? string.Empty : string.Join(" / ", containers);
    }

    /// <summary>The domain of a DN as a DNS name: <c>DC=corp,DC=example</c> becomes <c>corp.example</c>.</summary>
    public static string? DomainOf(string? dn)
    {
        var dcs = Split(dn)
            .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..])
            .ToList();

        return dcs.Count == 0 ? null : string.Join(".", dcs);
    }

    /// <summary>Builds a child DN, escaping the name so a comma or a plus in it cannot change the structure.</summary>
    public static string Child(string prefix, string name, string parentDn) =>
        $"{prefix}={Escape(name)},{parentDn}";

    /// <summary>RFC 4514 escaping for a relative name.</summary>
    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var leadingOrTrailingSpace = c == ' ' && (i == 0 || i == value.Length - 1);
            if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=' || leadingOrTrailingSpace || (c == '#' && i == 0))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    public static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
            }

            sb.Append(value[i]);
        }

        return sb.ToString().Trim('"');
    }

    /// <summary>True when <paramref name="dn"/> sits anywhere beneath <paramref name="ancestorDn"/>.</summary>
    public static bool IsUnder(string? dn, string? ancestorDn)
    {
        if (string.IsNullOrWhiteSpace(dn) || string.IsNullOrWhiteSpace(ancestorDn)) return false;
        return dn.Length > ancestorDn.Length
               && dn.EndsWith(ancestorDn, StringComparison.OrdinalIgnoreCase)
               && dn[dn.Length - ancestorDn.Length - 1] == ',';
    }
}
