namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Which LDAP attribute each on-prem-mastered Graph property actually lives in (M12.4), and what cannot be written
/// from this screen.
/// </summary>
/// <remarks>
/// <para>
/// Graph renames about half of them. <c>surname</c> is <c>sn</c>, <c>jobTitle</c> is <c>title</c>,
/// <c>officeLocation</c> is <c>physicalDeliveryOfficeName</c>, <c>city</c> is <c>l</c> and <c>state</c> is
/// <c>st</c> — the old X.500 short names, which is why guessing at this map produces an attribute the directory has
/// never heard of. The list is the one Entra Connect syncs, so writing here and reading in the cloud agree once the
/// sync has run.
/// </para>
/// <para>
/// A property this map refuses is not a failure to fix later: it is a write that would quietly do the wrong thing.
/// <c>manager</c> is a distinguished name rather than a display name, and a business phone list is several values
/// where AD's <c>telephoneNumber</c> is one — writing the first and dropping the rest would look like it worked.
/// </para>
/// </remarks>
public static class OnPremAttributeMap
{
    private const string ManagerRefusal =
        "AD stores the manager as a distinguished name, not a name. Set it in AD.";

    private const string MultiValueRefusal =
        "AD holds one business phone in telephoneNumber. Leave a single number here, or edit the rest in AD.";

    private const string NotMasteredRefusal =
        "This field is not mastered in AD, so it is written to Entra ID rather than on-prem.";

    /// <summary>
    /// The <see cref="Models.DirectoryUser.OnPremMasteredProperties"/> list, resolved to LDAP attribute names.
    /// <c>manager</c> is present so the map covers all twenty, and is refused at translation.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GraphToLdap { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["displayName"] = "displayName",
            ["givenName"] = "givenName",
            ["surname"] = "sn",
            ["userPrincipalName"] = "userPrincipalName",
            ["mail"] = "mail",
            ["mailNickname"] = "mailNickname",
            ["proxyAddresses"] = "proxyAddresses",
            ["jobTitle"] = "title",
            ["department"] = "department",
            ["companyName"] = "company",
            ["officeLocation"] = "physicalDeliveryOfficeName",
            ["employeeId"] = "employeeID",
            ["employeeType"] = "employeeType",
            ["businessPhones"] = "telephoneNumber",
            ["mobilePhone"] = "mobile",
            ["streetAddress"] = "streetAddress",
            ["city"] = "l",
            ["state"] = "st",
            ["postalCode"] = "postalCode",
            // Graph's country is the name ("United Kingdom"), which is AD's co. The two-letter code is c, and the
            // numeric one is countryCode; Entra Connect reads co, so writing c would never show up in the cloud.
            ["country"] = "co",
            ["manager"] = "manager",
        };

    /// <summary>
    /// The LDAP attribute this property is edited through, or null when this screen must not write it. Used to decide
    /// whether a greyed field can be unlocked at all, before there is any value to write.
    /// </summary>
    public static string? EditableLdapName(string graphProperty) =>
        Refusal(graphProperty) is null && GraphToLdap.TryGetValue(graphProperty, out var name) ? name : null;

    /// <summary>Why this property cannot be edited from the identity sheet, or null when it can.</summary>
    public static string? Refusal(string graphProperty)
    {
        if (string.Equals(graphProperty, "manager", StringComparison.OrdinalIgnoreCase)) return ManagerRefusal;
        return GraphToLdap.ContainsKey(graphProperty) ? null : NotMasteredRefusal;
    }

    /// <summary>Turns one edit from the identity sheet into an LDAP attribute and value, or a reason it cannot be.</summary>
    /// <param name="graphProperty">The Graph property name the field carries.</param>
    /// <param name="value">A <c>string</c>, a <c>string[]</c> from an array field, or null to clear.</param>
    public static OnPremTranslation Translate(string graphProperty, object? value)
    {
        if (Refusal(graphProperty) is { } refusal) return OnPremTranslation.Refused(refusal);

        var ldapName = GraphToLdap[graphProperty];

        switch (value)
        {
            case null:
                return new OnPremTranslation(ldapName, null, null);

            case string s:
                return new OnPremTranslation(ldapName, string.IsNullOrWhiteSpace(s) ? null : s.Trim(), null);

            case IReadOnlyList<string> list when list.Count == 0:
                return new OnPremTranslation(ldapName, null, null);

            case IReadOnlyList<string> list when list.Count == 1:
                return new OnPremTranslation(ldapName, list[0].Trim(), null);

            case IReadOnlyList<string>:
                return OnPremTranslation.Refused(MultiValueRefusal);

            default:
                return new OnPremTranslation(ldapName, value.ToString(), null);
        }
    }
}
