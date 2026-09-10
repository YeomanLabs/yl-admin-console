namespace AdminConsole.Core.Cache.Browse;

/// <summary>One group as a list page shows it.</summary>
public sealed record GroupListRow
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Mail { get; init; }
    public required string Kind { get; init; }
    public bool Dynamic { get; init; }
    public bool Synced { get; init; }
    public bool RoleAssignable { get; init; }
    public int MemberCount { get; init; }

    public string Primary => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}
