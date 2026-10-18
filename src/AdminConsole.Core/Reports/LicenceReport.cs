namespace AdminConsole.Core.Reports;

/// <summary>M19.2: seats, how they are assigned, and licensed accounts nobody is using.</summary>
public sealed record LicenceReport
{
    public required DateTimeOffset TakenAt { get; init; }

    public IReadOnlyList<LicenceSkuRow> Skus { get; init; } = [];

    public IReadOnlyList<LicensedUserRow> Flagged { get; init; } = [];

    /// <summary>Everyone holding any licence (since 0.3.3; empty in a report built before), sorted by name.</summary>
    public IReadOnlyList<LicensedUserRow> Holders { get; init; } = [];

    public IEnumerable<LicensedUserRow> HoldersOf(string skuId) =>
        Holders.Where(h => h.SkuIds.Contains(skuId, StringComparer.OrdinalIgnoreCase));

    /// <summary>Set when sign-in activity could not be read (no P1, or no AuditLog.Read.All): "quiet" is then not judged.</summary>
    public string? QuietUnavailable { get; init; }

    public int QuietDays { get; init; } = 90;
}
