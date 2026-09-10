namespace AdminConsole.Core.OnPrem;

/// <summary>One translated edit: the LDAP attribute to write, or the reason the write is refused.</summary>
/// <param name="LdapName">The attribute to set, or null when refused.</param>
/// <param name="Value">The value to write; null clears the attribute.</param>
/// <param name="Refusal">Why this edit cannot go to AD, in words for the field's inline message.</param>
public sealed record OnPremTranslation(string? LdapName, string? Value, string? Refusal)
{
    public bool CanWrite => Refusal is null && LdapName is not null;

    public static OnPremTranslation Refused(string reason) => new(null, null, reason);
}
