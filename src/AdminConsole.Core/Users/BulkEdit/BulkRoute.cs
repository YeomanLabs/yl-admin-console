namespace AdminConsole.Core.Users.BulkEdit;

/// <summary>Where one property of one user is written.</summary>
public enum BulkRoute
{
    /// <summary>A Graph PATCH: a cloud user, or a synced user's cloud-mastered property such as usage location.</summary>
    Entra,

    /// <summary>An LDAP modify on the AD object behind a synced user; Entra Connect carries it to the cloud.</summary>
    ActiveDirectory,

    /// <summary>Already that value, so nothing is sent.</summary>
    Unchanged,

    /// <summary>Cannot be written from here; <see cref="BulkPlannedChange.Reason"/> says why.</summary>
    Refused,
}
