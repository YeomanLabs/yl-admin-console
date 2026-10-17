namespace AdminConsole.Core.Pim;

/// <summary>One directory role the signed-in admin holds through PIM: eligible, and perhaps active right now.</summary>
public sealed record PimRole
{
    public required string RoleDefinitionId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The built-in role's template id — what the grey rule compares on. Same as the definition id for built-ins.</summary>
    public required string RoleTemplateId { get; init; }

    /// <summary>When the eligibility itself ends; null for a permanent eligibility.</summary>
    public DateTimeOffset? EligibleUntil { get; init; }

    /// <summary>True when eligible through a group rather than directly. Activation works the same either way.</summary>
    public bool ViaGroup { get; init; }

    public bool IsActive { get; init; }

    public DateTimeOffset? ActiveUntil { get; init; }

    /// <summary>Active through a permanent assignment, not an activation — nothing to activate, nothing that expires.</summary>
    public bool IsPermanent { get; init; }

    public string StateText => IsPermanent
        ? "Assigned permanently"
        : IsActive
            ? ActiveUntil is { } until ? $"Active until {until.ToLocalTime():g}" : "Active"
            : EligibleUntil is { } end ? $"Eligible until {end.ToLocalTime():d}" : "Eligible";
}
