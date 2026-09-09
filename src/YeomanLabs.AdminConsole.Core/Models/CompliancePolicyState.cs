using System.Text.Json;

namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>One row of <c>/deviceManagement/managedDevices/{id}/deviceCompliancePolicyStates</c> (doc 04 §Intune, UX §5 Compliance section).</summary>
public sealed record CompliancePolicyState
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>compliant | noncompliant | inGracePeriod | error | conflict | notApplicable | unknown</summary>
    public string? State { get; init; }

    public int? Version { get; init; }
    public string? PlatformType { get; init; }
    public int? SettingCount { get; init; }

    /// <summary>Filled on demand for non-compliant policies (reason on failure).</summary>
    public IReadOnlyList<ComplianceSettingState> SettingStates { get; init; } = [];

    public bool IsCompliant => string.Equals(State, "compliant", StringComparison.OrdinalIgnoreCase);
    public bool IsNotApplicable => string.Equals(State, "notApplicable", StringComparison.OrdinalIgnoreCase);
    public bool NeedsReason => !IsCompliant && !IsNotApplicable && !string.Equals(State, "unknown", StringComparison.OrdinalIgnoreCase);

    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;

    public static CompliancePolicyState FromJson(JsonElement e) => new()
    {
        Id = e.GetString("id") ?? throw new JsonException("compliance policy state without id"),
        DisplayName = e.GetString("displayName"),
        State = e.GetString("state"),
        Version = e.GetInt("version"),
        PlatformType = e.GetString("platformType"),
        SettingCount = e.GetInt("settingCount"),
    };
}
