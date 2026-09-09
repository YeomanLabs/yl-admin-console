namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>Cache projection of an Entra device object (docs/05-DATA-MODEL.md <c>devices</c>), not the Intune record.</summary>
public sealed record EntraDevice
{
    public required string Id { get; init; }

    /// <summary>Graph <c>deviceId</c>; equals <c>azureADDeviceId</c> on the Intune object.</summary>
    public required string DeviceId { get; init; }

    public string? DisplayName { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OperatingSystemVersion { get; init; }
    public string? TrustType { get; init; }
    public bool? IsCompliant { get; init; }
    public bool? IsManaged { get; init; }
    public bool? AccountEnabled { get; init; }
    public DateTimeOffset? ApproximateLastSignInDateTime { get; init; }
    public required string Raw { get; init; }
}
