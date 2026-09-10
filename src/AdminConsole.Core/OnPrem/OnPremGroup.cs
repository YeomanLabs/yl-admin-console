namespace AdminConsole.Core.OnPrem;

/// <summary>An Active Directory group (M12.1).</summary>
public sealed record OnPremGroup
{
    public required Guid ObjectGuid { get; init; }
    public required string DistinguishedName { get; init; }
    public required string SamAccountName { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Mail { get; init; }

    public required GroupScope Scope { get; init; }
    public required GroupCategory Category { get; init; }

    /// <summary>Direct members only. A nested group appears as one entry, not as its members.</summary>
    public IReadOnlyList<string> MemberDns { get; init; } = [];

    /// <summary>
    /// True for the groups AD will not let anyone delete or rename — Domain Users, Domain Admins and the rest.
    /// Worth knowing before offering a button that cannot work.
    /// </summary>
    public bool IsCritical { get; init; }

    public int MemberCount => MemberDns.Count;

    public string Primary => string.IsNullOrWhiteSpace(DisplayName) ? SamAccountName : DisplayName;

    public string? ParentOuDn => DistinguishedNames.Parent(DistinguishedName);

    /// <summary>How the console says it: "Global security group", "Universal distribution group".</summary>
    public string ScopeText => $"{Scope switch
    {
        GroupScope.DomainLocal => "Domain local",
        var s => s.ToString(),
    }} {Category.ToString().ToLowerInvariant()} group";
}
