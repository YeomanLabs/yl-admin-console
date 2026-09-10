using System.Globalization;
using System.Text;
using System.DirectoryServices.Protocols;

namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Reading values out of an LDAP search result, and the encodings AD insists on (M12.2).
/// </summary>
/// <remarks>
/// Every one of these exists because the naive version is wrong. Timestamps are 100-nanosecond intervals since 1601
/// with two different "never" sentinels. <c>objectGUID</c> comes back as raw bytes and has to be escaped byte by byte
/// to be searched for. A password is UTF-16 with literal quotes around it. Getting any of these subtly wrong produces
/// a result that looks plausible and is not.
/// </remarks>
public static class LdapAttributes
{
    public static string? String(SearchResultEntry entry, string attribute)
    {
        var values = entry.Attributes[attribute];
        if (values is null || values.Count == 0) return null;
        var value = values[0];
        return value switch
        {
            string s => string.IsNullOrWhiteSpace(s) ? null : s,
            byte[] b => Encoding.UTF8.GetString(b),
            _ => value?.ToString(),
        };
    }

    public static IReadOnlyList<string> Strings(SearchResultEntry entry, string attribute)
    {
        var values = entry.Attributes[attribute];
        if (values is null || values.Count == 0) return [];

        var list = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is string s) list.Add(s);
            else if (values[i] is byte[] b) list.Add(Encoding.UTF8.GetString(b));
        }

        return list;
    }

    public static int Int(SearchResultEntry entry, string attribute, int fallback = 0) =>
        int.TryParse(String(entry, attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static byte[]? Bytes(SearchResultEntry entry, string attribute)
    {
        var values = entry.Attributes[attribute];
        if (values is null || values.Count == 0) return null;
        return values[0] as byte[];
    }

    public static Guid? Guid(SearchResultEntry entry, string attribute)
    {
        var bytes = Bytes(entry, attribute);
        return bytes is { Length: 16 } ? new Guid(bytes) : null;
    }

    /// <summary>
    /// An AD timestamp: 100-nanosecond intervals since 1601-01-01 UTC. Both 0 and <see cref="long.MaxValue"/> mean
    /// "never", and treating either as a date produces 1601 or 30828, which is how you end up showing an admin that
    /// somebody last signed in four centuries ago.
    /// </summary>
    public static DateTimeOffset? FileTime(SearchResultEntry entry, string attribute)
    {
        var raw = String(entry, attribute);
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) return null;
        if (ticks <= 0 || ticks == long.MaxValue) return null;

        try
        {
            return DateTimeOffset.FromFileTime(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A generalised time, as <c>whenCreated</c> uses: <c>20260910083000.0Z</c>.</summary>
    public static DateTimeOffset? GeneralizedTime(SearchResultEntry entry, string attribute)
    {
        var raw = String(entry, attribute);
        if (raw is null || raw.Length < 14) return null;

        return DateTimeOffset.TryParseExact(raw[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Escapes a GUID for an LDAP filter, as <c>\a1\b2…</c>. This is how an object is found by identity rather than
    /// by path, and the byte order is the GUID's own little-endian layout — not the text form.
    /// </summary>
    public static string GuidFilter(Guid guid)
    {
        var sb = new StringBuilder(48);
        foreach (var b in guid.ToByteArray())
        {
            sb.Append('\\').Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes a value for use inside an LDAP filter (RFC 4515). Without it, a search box is an injection point:
    /// a lone <c>*</c> or <c>)</c> typed by an admin changes the filter rather than matching text.
    /// </summary>
    public static string EscapeFilter(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                case '/': sb.Append("\\2f"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A password as AD wants it for <c>unicodePwd</c>: UTF-16LE, wrapped in literal double quotes. The quotes are
    /// part of the encoding, not a delimiter, and omitting them fails with an unhelpful constraint violation.
    /// </summary>
    public static byte[] EncodePassword(string password) =>
        Encoding.Unicode.GetBytes($"\"{password}\"");

    /// <summary>Splits AD's <c>groupType</c> bit field into the two things a person actually thinks about.</summary>
    public static (GroupScope Scope, GroupCategory Category) GroupType(int groupType)
    {
        const int global = 0x00000002;
        const int domainLocal = 0x00000004;
        const int universal = 0x00000008;
        const int securityEnabled = unchecked((int)0x80000000);

        var scope = (groupType & universal) != 0 ? GroupScope.Universal
            : (groupType & domainLocal) != 0 ? GroupScope.DomainLocal
            : GroupScope.Global;

        // Deliberately not "groupType < 0": the sign bit is the security flag, and relying on that is the kind of
        // cleverness that stops being true the first time anyone touches it.
        var category = (groupType & securityEnabled) != 0 ? GroupCategory.Security : GroupCategory.Distribution;
        _ = global;
        return (scope, category);
    }
}
