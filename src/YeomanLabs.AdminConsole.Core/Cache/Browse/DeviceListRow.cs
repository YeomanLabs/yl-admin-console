namespace YeomanLabs.AdminConsole.Core.Cache.Browse;

/// <summary>
/// One device as a list page shows it. A device may be in Intune, in Entra, or in both, so the list unions the two and
/// this row says which — an Entra-only row has no serial or compliance state, and that absence is information.
/// </summary>
public sealed record DeviceListRow
{
    /// <summary>The Intune device id where there is one, otherwise the Entra object id. What the page opens.</summary>
    public required string Id { get; init; }

    public required DeviceSource Source { get; init; }
    public string? DeviceName { get; init; }
    public string? Serial { get; init; }
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    public string? Os { get; init; }
    public string? OsVersion { get; init; }

    /// <summary>Intune compliance state; null on an Entra-only device, which Intune has never evaluated.</summary>
    public string? Compliance { get; init; }

    public bool? Encrypted { get; init; }
    public string? PrimaryUserId { get; init; }
    public string? PrimaryUserName { get; init; }
    public string? PrimaryUserUpn { get; init; }

    /// <summary>Last Intune check-in, or last Entra sign-in for an Entra-only device.</summary>
    public DateTimeOffset? LastActivity { get; init; }

    public DateTimeOffset? Enrolled { get; init; }
    public bool Autopilot { get; init; }
    public string? OwnerType { get; init; }
    public string? TrustType { get; init; }

    public bool IsIntuneManaged => Source == DeviceSource.Intune;

    public string Primary => string.IsNullOrWhiteSpace(DeviceName) ? (Serial ?? Id) : DeviceName;
}
