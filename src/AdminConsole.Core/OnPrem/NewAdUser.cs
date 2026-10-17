namespace AdminConsole.Core.OnPrem;

/// <summary>
/// A user to create in AD (M17.2). <see cref="Attributes"/> uses LDAP attribute names (<c>department</c>, <c>title</c>,
/// <c>physicalDeliveryOfficeName</c>) because that is what a hybrid template stores and what the directory is written with.
/// </summary>
public sealed record NewAdUser
{
    public required string ParentOuDn { get; init; }

    /// <summary>The CN and the display name.</summary>
    public required string Name { get; init; }

    public required string GivenName { get; init; }
    public required string Surname { get; init; }

    /// <summary>pre-Windows 2000 name; AD allows 20 characters.</summary>
    public required string SamAccountName { get; init; }

    public required string UserPrincipalName { get; init; }

    /// <summary>Never logged, never stored; it reaches AD and the show-once screen and nowhere else.</summary>
    public required string Password { get; init; }

    public bool MustChangePassword { get; init; } = true;

    /// <summary>The primary SMTP address Entra Connect will carry to the cloud (<c>mail</c>), when one is set.</summary>
    public string? Mail { get; init; }

    /// <summary><c>SMTP:primary</c> and <c>smtp:extra</c> addresses — mastered in AD for a synced user, so set here.</summary>
    public IReadOnlyList<string> ProxyAddresses { get; init; } = [];

    public IReadOnlyDictionary<string, string?> Attributes { get; init; } = new Dictionary<string, string?>();
}
