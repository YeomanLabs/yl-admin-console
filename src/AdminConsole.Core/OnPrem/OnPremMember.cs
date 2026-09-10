namespace AdminConsole.Core.OnPrem;

/// <summary>
/// One member of a group, resolved enough to draw a row (M12.1). A nested group appears as itself, not as the
/// people inside it — flattening here would hide the structure an admin is usually trying to see.
/// </summary>
public sealed record OnPremMember
{
    public required Guid ObjectGuid { get; init; }
    public required string DistinguishedName { get; init; }
    public required string Name { get; init; }
    public required OnPremMemberKind Kind { get; init; }
    public string? SamAccountName { get; init; }

    /// <summary>False when the member account is disabled — a disabled account in an access group is worth seeing.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The member's own scope when it is a group, null otherwise. Carried because what a group may contain depends
    /// on it (M12.5), and re-reading every member to find out would turn one screen into dozens of queries.
    /// </summary>
    public GroupScope? Scope { get; init; }
}
