using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>Cache projection of an Entra device object (docs/05-DATA-MODEL.md <c>devices</c>), not the Intune record.</summary>
public sealed record EntraDevice
{
    public required string Id { get; init; }

    /// <summary>Graph <c>deviceId</c>; equals <c>azureADDeviceId</c> on the Intune object.</summary>
    public required string DeviceId { get; init; }

    public string? DisplayName { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OperatingSystemVersion { get; init; }

    /// <summary>AzureAd (joined) | ServerAd (hybrid joined) | Workplace (registered)</summary>
    public string? TrustType { get; init; }

    public bool? IsCompliant { get; init; }
    public bool? IsManaged { get; init; }
    public bool? AccountEnabled { get; init; }
    public DateTimeOffset? ApproximateLastSignInDateTime { get; init; }
    public required string Raw { get; init; }

    public bool IsDisabled => AccountEnabled == false;
    public bool IsHybridJoined => string.Equals(TrustType, "ServerAd", StringComparison.OrdinalIgnoreCase);
    public bool IsRegisteredOnly => string.Equals(TrustType, "Workplace", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Badges
    {
        get
        {
            var list = new List<string>(3);
            if (IsDisabled) list.Add("Disabled");
            if (IsCompliant == false) list.Add("Non-compliant");
            if (IsHybridJoined) list.Add("Hybrid");
            return list;
        }
    }

    public static EntraDevice FromJson(JsonElement e) => new()
    {
        Id = e.GetString("id") ?? throw new JsonException("device without id"),
        DeviceId = e.GetString("deviceId") ?? string.Empty,
        DisplayName = e.GetString("displayName"),
        OperatingSystem = e.GetString("operatingSystem"),
        OperatingSystemVersion = e.GetString("operatingSystemVersion"),
        TrustType = e.GetString("trustType"),
        IsCompliant = e.GetBool("isCompliant"),
        IsManaged = e.GetBool("isManaged"),
        AccountEnabled = e.GetBool("accountEnabled"),
        ApproximateLastSignInDateTime = e.GetDate("approximateLastSignInDateTime"),
        Raw = e.GetRawText(),
    };

    public JsonDocument ParseRaw() => JsonDocument.Parse(Raw);
}
