namespace YeomanLabs.AdminConsole.Core.Models;

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
    public required string Raw { get; init; }

    public int Available => Math.Max(0, Enabled - Consumed);
    public string DisplayName => FriendlyName ?? SkuPartNumber;
}
