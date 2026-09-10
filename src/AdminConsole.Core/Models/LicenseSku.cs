using System.Text.Json;
using AdminConsole.Core.Licensing;

namespace AdminConsole.Core.Models;

/// <summary>Cache projection of a <c>subscribedSku</c> (docs/05-DATA-MODEL.md <c>skus</c>).</summary>
public sealed record LicenseSku
{
    public required string SkuId { get; init; }
    public required string SkuPartNumber { get; init; }

    /// <summary>Static friendly name (e.g. "Microsoft 365 E3"), or null when the part number is unknown.</summary>
    public string? FriendlyName { get; init; }

    /// <summary><c>prepaidUnits.enabled</c></summary>
    public required int Enabled { get; init; }

    public required int Consumed { get; init; }

    /// <summary><c>capabilityStatus</c>: Enabled | Warning | Suspended | Deleted | LockedOut</summary>
    public string? CapabilityStatus { get; init; }

    public required string Raw { get; init; }

    public int Available => Math.Max(0, Enabled - Consumed);
    public string DisplayName => FriendlyName ?? SkuPartNumber;
    public bool IsEnabled => string.Equals(CapabilityStatus, "Enabled", StringComparison.OrdinalIgnoreCase);

    public static LicenseSku FromJson(JsonElement e)
    {
        var partNumber = e.GetString("skuPartNumber") ?? string.Empty;
        return new LicenseSku
        {
            SkuId = e.GetString("skuId") ?? throw new JsonException("subscribedSku without skuId"),
            SkuPartNumber = partNumber,
            FriendlyName = SkuNames.For(partNumber),
            Enabled = e.GetObject("prepaidUnits")?.GetInt("enabled") ?? 0,
            Consumed = e.GetInt("consumedUnits") ?? 0,
            CapabilityStatus = e.GetString("capabilityStatus"),
            Raw = e.GetRawText(),
        };
    }
}
