namespace AdminConsole.Core.OnPrem;

/// <summary>What a group member is. AD lets all four be members, and each behaves differently when removed.</summary>
public enum OnPremMemberKind
{
    User,
    Group,
    Computer,
    Other,
}
