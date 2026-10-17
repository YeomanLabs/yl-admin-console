using System.Globalization;
using System.Text.Json;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// Reads Exchange's row shapes. Every property may carry an <c>@odata.type</c> sibling, several arrive as strings that
/// need parsing, and Full Access rights come back comma-joined inside a one-element array (doc 13 §1).
/// </summary>
public static class ExchangeValues
{
    /// <summary>The principal Exchange uses for a mailbox's access to itself. Present in both permission lists; never shown.</summary>
    public const string SelfPrincipal = @"NT AUTHORITY\SELF";

    public const string SelfSid = "S-1-5-10";

    public static string? String(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null,
        } : null;

    /// <summary>Null and empty are the same thing in Exchange's output (<c>LitigationHoldOwner: ""</c>), so both read as null.</summary>
    public static string? NonEmpty(JsonElement item, string name) => String(item, name) is { Length: > 0 } s ? s : null;

    public static bool? Bool(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null,
        };
    }

    public static bool IsTrue(JsonElement item, string name) => Bool(item, name) == true;

    public static long? Long(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    public static DateTimeOffset? Date(JsonElement item, string name) =>
        String(item, name) is { Length: > 0 } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    public static ExchangeSize Size(JsonElement item, string name) => ExchangeSize.Parse(String(item, name));

    /// <summary>A string collection, tolerating a single string where a collection was expected.</summary>
    public static IReadOnlyList<string> Strings(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var v)) return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => s.Length > 0)
                .ToList(),
            JsonValueKind.String when v.GetString() is { Length: > 0 } s => [s],
            _ => [],
        };
    }

    /// <summary>
    /// Access rights, split. <c>Get-MailboxPermission</c> answers <c>["FullAccess, ReadPermission"]</c> — one element
    /// holding a comma-separated list — while <c>Get-RecipientPermission</c> answers <c>["SendAs"]</c>.
    /// </summary>
    public static IReadOnlyList<string> Rights(JsonElement item, string name = "AccessRights") =>
        Strings(item, name)
            .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsSelf(string? principal, string? sid = null) =>
        string.Equals(principal, SelfPrincipal, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sid, SelfSid, StringComparison.OrdinalIgnoreCase);
}
