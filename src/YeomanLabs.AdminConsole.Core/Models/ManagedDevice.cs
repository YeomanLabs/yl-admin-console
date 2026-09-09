namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>Cache projection of an Intune managed device (docs/05-DATA-MODEL.md <c>managed_devices</c>).</summary>
public sealed record ManagedDevice
{
    public required string Id { get; init; }
    public string? AzureAdDeviceId { get; init; }
    public string? DeviceName { get; init; }
    public string? SerialNumber { get; init; }
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OsVersion { get; init; }

    /// <summary>compliant | noncompliant | inGracePeriod | unknown | conflict | error</summary>
    public string? ComplianceState { get; init; }

    public bool? IsEncrypted { get; init; }
    public string? PrimaryUserId { get; init; }
    public string? PrimaryUserUpn { get; init; }
    public string? PrimaryUserDisplayName { get; init; }
    public DateTimeOffset? LastSyncDateTime { get; init; }
    public DateTimeOffset? EnrolledDateTime { get; init; }
    public bool? AutopilotEnrolled { get; init; }
    public string? OwnerType { get; init; }
    public required string Raw { get; init; }
}
