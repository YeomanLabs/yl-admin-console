using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>One entry of <c>user.licenseAssignmentStates</c>: which SKU, whether it came from a group, and any error (doc 04 §Users).</summary>
public sealed record LicenseAssignmentState
{
    public required string SkuId { get; init; }

    /// <summary>Group object id when the license is group-based (cannot be removed per user), else null.</summary>
    public string? AssignedByGroup { get; init; }

    /// <summary>Active | ActiveWithError | Disabled | Error</summary>
    public string? State { get; init; }

    /// <summary>e.g. CountViolation, MutuallyExclusiveViolation, DependencyViolation, ProhibitedInUsageLocationViolation</summary>
    public string? Error { get; init; }

    public IReadOnlyList<string> DisabledPlans { get; init; } = [];
    public DateTimeOffset? LastUpdatedDateTime { get; init; }

    public bool IsGroupAssigned => !string.IsNullOrEmpty(AssignedByGroup);
    public bool HasError => !string.IsNullOrEmpty(Error) && !string.Equals(Error, "None", StringComparison.OrdinalIgnoreCase);

    public static LicenseAssignmentState FromJson(JsonElement e) => new()
    {
        SkuId = e.GetString("skuId") ?? throw new JsonException("licenseAssignmentState without skuId"),
        AssignedByGroup = e.GetString("assignedByGroup"),
        State = e.GetString("state"),
        Error = e.GetString("error"),
        DisabledPlans = e.GetStrings("disabledPlans"),
        LastUpdatedDateTime = e.GetDate("lastUpdatedDateTime"),
    };
}
