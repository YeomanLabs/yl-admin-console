using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>One setting inside a compliance policy state (<c>.../deviceCompliancePolicyStates/{id}/settingStates</c>): the failure reason behind a non-compliant policy.</summary>
public sealed record ComplianceSettingState
{
    public required string Id { get; init; }
    public string? Setting { get; init; }
    public string? SettingName { get; init; }

    /// <summary>compliant | noncompliant | error | conflict | notApplicable | unknown</summary>
    public string? State { get; init; }

    public string? ErrorDescription { get; init; }
    public string? CurrentValue { get; init; }

    public bool IsCompliant => string.Equals(State, "compliant", StringComparison.OrdinalIgnoreCase) || string.Equals(State, "notApplicable", StringComparison.OrdinalIgnoreCase);

    public string Label => SettingName ?? Setting ?? Id;

    public static ComplianceSettingState FromJson(JsonElement e) => new()
    {
        Id = e.GetString("id") ?? Guid.NewGuid().ToString("N"),
        Setting = e.GetString("setting"),
        SettingName = e.GetString("settingName"),
        State = e.GetString("state"),
        ErrorDescription = e.GetString("errorDescription"),
        CurrentValue = e.GetString("currentValue"),
    };
}
