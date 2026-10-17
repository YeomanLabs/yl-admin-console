namespace AdminConsole.Core.Reports;

/// <summary>One subscription in the licence report (M19.2).</summary>
public sealed record LicenceSkuRow
{
    public required string SkuId { get; init; }
    public required string PartNumber { get; init; }
    public required string Name { get; init; }

    /// <summary>Seats bought and active — the portal's "available" total.</summary>
    public int Enabled { get; init; }

    /// <summary>Seats past their end date, still usable for now.</summary>
    public int Warning { get; init; }

    public int Suspended { get; init; }

    /// <summary>Assigned seats — the portal's "assigned" figure.</summary>
    public int Consumed { get; init; }

    public int Available => Math.Max(0, Enabled + Warning - Consumed);

    /// <summary>People holding it directly, through a group, or both (both counts once in each).</summary>
    public int Direct { get; init; }

    public int ViaGroup { get; init; }

    public int Disabled { get; init; }

    public int Quiet { get; init; }

    /// <summary>
    /// Lapsed: no active or grace-period seats, only suspended ones. People can still hold it — Entra keeps the
    /// assignment — so its assigned count is real but belongs to no seat that is paid for.
    /// </summary>
    public bool IsSuspended => Enabled + Warning == 0 && Suspended > 0;

    public string StateText => IsSuspended ? $"Suspended — {Suspended:N0} seats lapsed, {Consumed:N0} still assigned" : string.Empty;

    /// <summary>A free subscription (Power Automate Free, Teams Exploratory…) whose "seats" are a ceiling, not a purchase.</summary>
    public bool LooksFree => Enabled >= 10000;
}
