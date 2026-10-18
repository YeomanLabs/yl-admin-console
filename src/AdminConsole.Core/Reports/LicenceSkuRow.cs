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

    public override string ToString() => $"{Name}, {Consumed} of {Enabled + Warning} seats assigned{(IsSuspended ? ", suspended" : string.Empty)}";

    /// <summary>A free subscription (Power Automate Free, Teams Exploratory…) whose "seats" are a ceiling, not a purchase.</summary>
    public bool LooksFree => Enabled >= 10000;

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so after a rebuild a row equal
    // to the one before took over that row's detached peer and dropped out of what a screen reader can reach (0.3.3).
    public bool Equals(LicenceSkuRow? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
