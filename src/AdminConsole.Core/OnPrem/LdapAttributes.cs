using System.Globalization;
using System.Text;
using System.DirectoryServices.Protocols;
using System.Security.AccessControl;
using System.Security.Principal;

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

    private const int GroupGlobal = 0x00000002;
    private const int GroupDomainLocal = 0x00000004;
    private const int GroupUniversal = 0x00000008;
    private const int GroupSecurityEnabled = unchecked((int)0x80000000);

    /// <summary>Splits AD's <c>groupType</c> bit field into the two things a person actually thinks about.</summary>
    public static (GroupScope Scope, GroupCategory Category) GroupType(int groupType)
    {
        var scope = (groupType & GroupUniversal) != 0 ? GroupScope.Universal
            : (groupType & GroupDomainLocal) != 0 ? GroupScope.DomainLocal
            : GroupScope.Global;

        // Deliberately not "groupType < 0": the sign bit is the security flag, and relying on that is the kind of
        // cleverness that stops being true the first time anyone touches it.
        var category = (groupType & GroupSecurityEnabled) != 0 ? GroupCategory.Security : GroupCategory.Distribution;
        return (scope, category);
    }

    /// <summary>
    /// Composes the <c>groupType</c> value for a scope and category (M12.5), which is what a conversion writes.
    /// </summary>
    /// <remarks>
    /// Exactly one scope bit is set. Carrying the old value forward and flipping bits would leave two scope bits set
    /// on any conversion, which AD rejects — so the value is built from nothing each time. Nothing else lives in
    /// this attribute, so there is no other state to preserve.
    /// </remarks>
    public static int GroupTypeValue(GroupScope scope, GroupCategory category)
    {
        var value = scope switch
        {
            GroupScope.DomainLocal => GroupDomainLocal,
            GroupScope.Universal => GroupUniversal,
            _ => GroupGlobal,
        };

        return category == GroupCategory.Security ? value | GroupSecurityEnabled : value;
    }

    /// <summary>
    /// Whether a binary security descriptor denies Everyone the right to delete the object (M12.6) — which is how
    /// AD stores the "protect object from accidental deletion" checkbox.
    /// </summary>
    /// <remarks>
    /// There is no attribute for the checkbox. Ticking it writes a deny ACE for Everyone covering Delete and Delete
    /// Subtree, and the only way to know it is set is to read the DACL and look. Without this, the refusal that
    /// follows is a bare access-denied, indistinguishable from a delegation the admin has not been given — so they
    /// go and audit rights that were never the problem.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool DeniesDeletion(byte[]? securityDescriptor)
    {
        if (securityDescriptor is null || securityDescriptor.Length == 0) return false;

        // ActiveDirectoryRights: Delete is 0x10000 and DeleteTree is 0x40. Both live in the standard access mask, so
        // a raw descriptor can be read without System.DirectoryServices and its Windows-only object model.
        const int delete = 0x00010000;
        const int deleteTree = 0x00000040;

        try
        {
            var descriptor = new CommonSecurityDescriptor(true, true, securityDescriptor, 0);
            if (descriptor.DiscretionaryAcl is not { } dacl) return false;

            foreach (var rule in dacl.Cast<CommonAce>())
            {
                if (rule.AceType != AceType.AccessDenied) continue;
                if ((rule.AccessMask & (delete | deleteTree)) == 0) continue;
                if (rule.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid)) return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidCastException)
        {
            // A descriptor shape this cannot read is not evidence either way, and guessing "protected" would block a
            // delete AD would have allowed.
            return false;
        }
    }
}
