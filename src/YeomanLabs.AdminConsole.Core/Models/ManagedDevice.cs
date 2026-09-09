using System.Text.Json;

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

    /// <summary>compliant | noncompliant | inGracePeriod | unknown | conflict | error | configManager</summary>
    public string? ComplianceState { get; init; }

    public bool? IsEncrypted { get; init; }
    public string? PrimaryUserId { get; init; }
    public string? PrimaryUserUpn { get; init; }
    public string? PrimaryUserDisplayName { get; init; }
    public DateTimeOffset? LastSyncDateTime { get; init; }
    public DateTimeOffset? EnrolledDateTime { get; init; }

    /// <summary>Derived on v1.0 as <c>enrollmentProfileName != null</c> (the property itself is beta-only; DECISIONS 2026-09-09).</summary>
    public bool? AutopilotEnrolled { get; init; }

    /// <summary><c>managedDeviceOwnerType</c>: company | personal | unknown</summary>
    public string? OwnerType { get; init; }

    /// <summary><c>deviceEnrollmentType</c>, e.g. windowsAzureADJoin, windowsCoManagement, windowsBulkAzureDomainJoin.</summary>
    public string? EnrollmentType { get; init; }

    public required string Raw { get; init; }

    public bool IsCompliant => string.Equals(ComplianceState, "compliant", StringComparison.OrdinalIgnoreCase);
    public bool IsNonCompliant => string.Equals(ComplianceState, "noncompliant", StringComparison.OrdinalIgnoreCase);
    public bool IsInGracePeriod => string.Equals(ComplianceState, "inGracePeriod", StringComparison.OrdinalIgnoreCase);
    public bool IsPersonal => string.Equals(OwnerType, "personal", StringComparison.OrdinalIgnoreCase);

    /// <summary>No check-in for over 30 days; the usual "is this device still alive" threshold.</summary>
    public bool IsStale(DateTimeOffset now) => LastSyncDateTime is { } t && now - t > TimeSpan.FromDays(30);

    public IReadOnlyList<string> Badges
    {
        get
        {
            var list = new List<string>(4);
            if (IsNonCompliant) list.Add("Non-compliant");
            else if (IsInGracePeriod) list.Add("Grace period");
            if (IsEncrypted == false) list.Add("Not encrypted");
            if (AutopilotEnrolled == true) list.Add("Autopilot");
            if (IsPersonal) list.Add("Personal");
            return list;
        }
    }

    public static ManagedDevice FromJson(JsonElement e) => new()
    {
        Id = e.GetString("id") ?? throw new JsonException("managedDevice without id"),
        AzureAdDeviceId = e.GetString("azureADDeviceId"),
        DeviceName = e.GetString("deviceName"),
        SerialNumber = e.GetString("serialNumber"),
        Model = e.GetString("model"),
        Manufacturer = e.GetString("manufacturer"),
        OperatingSystem = e.GetString("operatingSystem"),
        OsVersion = e.GetString("osVersion"),
        ComplianceState = e.GetString("complianceState"),
        IsEncrypted = e.GetBool("isEncrypted"),
        PrimaryUserId = NullIfEmpty(e.GetString("userId")),
        PrimaryUserUpn = NullIfEmpty(e.GetString("userPrincipalName")),
        PrimaryUserDisplayName = NullIfEmpty(e.GetString("userDisplayName")),
        LastSyncDateTime = e.GetDate("lastSyncDateTime"),
        EnrolledDateTime = e.GetDate("enrolledDateTime"),
        AutopilotEnrolled = e.TryGetProperty("enrollmentProfileName", out var p) ? p.ValueKind == JsonValueKind.String && p.GetString()!.Length > 0 : null,
        OwnerType = e.GetString("managedDeviceOwnerType"),
        EnrollmentType = e.GetString("deviceEnrollmentType"),
        Raw = e.GetRawText(),
    };

    public JsonDocument ParseRaw() => JsonDocument.Parse(Raw);

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
