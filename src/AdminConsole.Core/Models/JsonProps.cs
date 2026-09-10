using System.Globalization;
using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>Null-tolerant property readers for Graph JSON. Graph sends explicit nulls for selected-but-unset properties.</summary>
public static class JsonProps
{
    public static string? GetString(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? GetBool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    public static int? GetInt(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public static long? GetLong(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    public static DateTimeOffset? GetDate(this JsonElement e, string name)
    {
        var s = e.GetString(name);
        return s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    /// <summary>String items of an array property; empty when absent, null, or not an array.</summary>
    public static IReadOnlyList<string> GetStrings(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        }

        return list;
    }

    public static int ArrayLength(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    public static JsonElement? GetObject(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static bool IsRemoved(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("@removed", out var r) && r.ValueKind == JsonValueKind.Object;

    /// <summary>Digits only, e.g. "+1 (425) 555-0100" → "14255550100".</summary>
    public static string DigitsOnly(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s)
        {
            if (c is >= '0' and <= '9') buf[n++] = c;
        }

        return new string(buf[..n]);
    }
}
