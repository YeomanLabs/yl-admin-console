namespace AdminConsole.Core.Templates;

/// <summary>
/// The AD half of a hybrid template (M17.1): where the account is made, which AD groups it joins, and the AD attributes
/// that describe the role. LDAP names throughout, because AD is what gets written.
/// </summary>
public sealed record TemplateOnPrem
{
    /// <summary>The OU new accounts go in. Should be inside Entra Connect's sync scope, or the cloud half never happens.</summary>
    public required string OuDn { get; init; }

    public IReadOnlyList<TemplateAdGroup> Groups { get; init; } = [];

    /// <summary>LDAP attribute to value: <c>department</c>, <c>title</c>, <c>company</c>, <c>physicalDeliveryOfficeName</c>…</summary>
    public IReadOnlyDictionary<string, string?> Attributes { get; init; } = new Dictionary<string, string?>();

    /// <summary>The pre-Windows 2000 name, from the same tokens as the sign-in name; AD cuts it at 20 characters.</summary>
    public string SamRule { get; init; } = "{first}.{last}";

    /// <summary>The AD sign-in suffix (<c>contoso.com</c>); null uses the domain's own name.</summary>
    public string? UpnSuffix { get; init; }

    /// <summary>Extra addresses as rules (<c>{alias}@contoso.co.uk</c>), written to AD's <c>proxyAddresses</c> — AD masters them.</summary>
    public IReadOnlyList<string> AddressRules { get; init; } = [];

    /// <summary>The AD attributes a snapshot copies: the role's, never the person's (no phone, no employee id).</summary>
    public static readonly IReadOnlyList<string> SnapshotAttributes =
        ["department", "title", "company", "physicalDeliveryOfficeName", "l", "st", "co", "streetAddress", "postalCode", "employeeType"];
}

/// <summary>One AD group a hybrid template joins, by distinguished name, with its name for the preview.</summary>
public sealed record TemplateAdGroup(string Dn, string? Name);
