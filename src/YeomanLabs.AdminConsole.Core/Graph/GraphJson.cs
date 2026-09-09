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
    };
}
