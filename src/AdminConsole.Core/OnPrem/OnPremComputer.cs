namespace AdminConsole.Core.OnPrem;

/// <summary>
/// An Active Directory computer object (M12.1).
/// </summary>
/// <remarks>
/// Not the same thing as an Entra device or an Intune managed device, and the console must never imply it is. A
/// machine can exist in AD only, in Entra only, in both when it is hybrid-joined, or in Intune and neither. Which of
/// those is true is usually the question being asked.
/// </remarks>
public sealed record OnPremComputer
{
    public required Guid ObjectGuid { get; init; }
    public required string DistinguishedName { get; init; }

    /// <summary>The account name, which for a computer ends in '$'.</summary>
    public required string SamAccountName { get; init; }

    public required string Name { get; init; }
    public string? DnsHostName { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OperatingSystemVersion { get; init; }
    public string? Description { get; init; }

    /// <summary>ADUC's Location field: free text, and about the only other thing people edit on a computer.</summary>
    public string? Location { get; init; }
    public required int UserAccountControl { get; init; }
    public DateTimeOffset? WhenCreated { get; init; }
    public DateTimeOffset? LastLogon { get; init; }

    public bool Enabled => !AccountFlags.IsDisabled(UserAccountControl);

    public string? ParentOuDn => DistinguishedNames.Parent(DistinguishedName);
}
