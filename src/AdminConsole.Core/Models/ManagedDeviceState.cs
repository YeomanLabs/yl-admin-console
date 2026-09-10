using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>The live, cheap-to-read slice of an Intune device for device cards: compliance, encryption, last check-in.</summary>
public sealed record ManagedDeviceState
{
    public const string Select = "id,complianceState,isEncrypted,lastSyncDateTime";

    public required string Id { get; init; }
    public string? ComplianceState { get; init; }
    public bool? IsEncrypted { get; init; }
    public DateTimeOffset? LastSyncDateTime { get; init; }

    public static ManagedDeviceState FromJson(JsonElement e) => new()
    {
        Id = e.GetString("id") ?? throw new JsonException("managedDevice state without id"),
        ComplianceState = e.GetString("complianceState"),
        IsEncrypted = e.GetBool("isEncrypted"),
        LastSyncDateTime = e.GetDate("lastSyncDateTime"),
    };
}
