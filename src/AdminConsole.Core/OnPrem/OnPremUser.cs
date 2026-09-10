namespace AdminConsole.Core.OnPrem;

/// <summary>
/// An Active Directory user, projected into the console's vocabulary rather than LDAP's (M12.1).
/// </summary>
/// <remarks>
/// Deliberately not <see cref="Models.DirectoryUser"/>: that is the Entra projection, and merging the two would hide
/// the thing an admin most needs to see on a hybrid tenant — which copy they are looking at, and which one is
/// authoritative for the field in front of them.
/// </remarks>
public sealed record OnPremUser
{
    /// <summary>The stable identity. Survives a rename and an OU move, unlike the distinguished name.</summary>
    public required Guid ObjectGuid { get; init; }

    /// <summary>Where it currently sits. Show this; never key on it.</summary>
    public required string DistinguishedName { get; init; }

    public required string SamAccountName { get; init; }
    public string? UserPrincipalName { get; init; }
    public string? DisplayName { get; init; }
    public string? GivenName { get; init; }
    public string? Surname { get; init; }
    public string? Mail { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public string? Company { get; init; }
    public string? Office { get; init; }
    public string? EmployeeId { get; init; }
    public string? Description { get; init; }

    // The rest of what ADUC shows on the General, Address and Telephones tabs (M12.11). Present so the AD-only page
    // can be an admin's whole page for somebody who exists only in AD, rather than a fact sheet with four buttons.
    public string? TelephoneNumber { get; init; }
    public string? Mobile { get; init; }
    public string? StreetAddress { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? PostalCode { get; init; }
    public string? Country { get; init; }

    /// <summary>When the account stops working, or null for never. AD has two encodings of "never"; both read as null.</summary>
    public DateTimeOffset? AccountExpires { get; init; }

    /// <summary>True when <c>pwdLastSet</c> is 0, which is how AD says the next sign-in must change it.</summary>
    public bool MustChangePasswordAtNextLogon { get; init; }

    /// <summary>Distinguished name of the manager, or null. Resolving it to a name costs another read.</summary>
    public string? ManagerDn { get; init; }

    /// <summary>Raw <c>userAccountControl</c>. Interpret it through <see cref="AccountFlags"/>, never by hand.</summary>
    public required int UserAccountControl { get; init; }

    public DateTimeOffset? WhenCreated { get; init; }
    public DateTimeOffset? LastLogon { get; init; }
    public DateTimeOffset? PasswordLastSet { get; init; }

    /// <summary>Non-null while the account is locked out.</summary>
    public DateTimeOffset? LockedOutUntil { get; init; }

    /// <summary>Distinguished names of the groups it is directly a member of. Excludes the primary group.</summary>
    public IReadOnlyList<string> MemberOfDns { get; init; } = [];

    /// <summary>
    /// The RID of the primary group, almost always 513 (Domain Users). It matters because the primary group is not in
    /// <see cref="MemberOfDns"/> and cannot be removed through a membership write.
    /// </summary>
    public int PrimaryGroupId { get; init; } = 513;

    /// <summary>Entra's <c>onPremisesImmutableId</c> is base64 of this, so it is what joins the two directories.</summary>
    public byte[]? ConsistencyGuid { get; init; }

    public bool Enabled => !AccountFlags.IsDisabled(UserAccountControl);

    public bool IsLockedOut => LockedOutUntil is not null && LockedOutUntil > DateTimeOffset.UtcNow;

    public bool PasswordNeverExpires => AccountFlags.PasswordNeverExpires(UserAccountControl);

    public bool AccountHasExpired => AccountExpires is { } when && when <= DateTimeOffset.UtcNow;

    public string Primary => string.IsNullOrWhiteSpace(DisplayName) ? SamAccountName : DisplayName;

    /// <summary>The OU it lives in — its distinguished name minus the leaf.</summary>
    public string? ParentOuDn => DistinguishedNames.Parent(DistinguishedName);
}
