using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YeomanLabs.AdminConsole.Core.Graph;

/// <summary>Serializer settings for everything that crosses the wire to Graph.</summary>
public static class GraphJson
{
    /// <summary>
    /// camelCase so C# DTOs serialize to Graph property names; nulls are kept because a PATCH with
    /// <c>"department": null</c> is a deliberate clear, not an omission.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,

        // Default escaping mangles '+' and anything non-ASCII, which makes phone numbers and non-English names
        // unreadable in the action log. Relaxed escaping is safe for a JSON HTTP body; the extra escaping it skips
        // only matters when JSON is embedded in HTML, which never happens here.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
