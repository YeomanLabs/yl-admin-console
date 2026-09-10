namespace AdminConsole.Core.OnPrem;

/// <summary>An organisational unit (M12.1). The tree is assembled from distinguished names, not from a parent link.</summary>
public sealed record OnPremOu
{
    public required Guid ObjectGuid { get; init; }
    public required string DistinguishedName { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// AD's "protect from accidental deletion" checkbox, which is a deny ACE. Knowing about it turns an access-denied
    /// error into a sentence naming the checkbox somebody has to untick.
    /// </summary>
    public bool ProtectedFromAccidentalDeletion { get; init; }

    public string? ParentDn => DistinguishedNames.Parent(DistinguishedName);
}
