namespace AdminConsole.Core.Auth;

/// <summary>Bound from the <c>Auth</c> section of appsettings.json. Public client: no secret.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Application (client) ID of the multi-tenant public client registration (doc 08).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Authority URL. Default is the multi-tenant "organizations" endpoint.</summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com/organizations";

    /// <summary>M20: the MSAL home account this tenant was last opened with. Null on a first sign-in.</summary>
    public string? AccountId { get; set; }

    /// <summary>M20: the directory this profile is for, so an account from another tenant is not picked by mistake.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// M20: true for a second tenant with no account yet. The prompt then offers the account picker instead of the
    /// Windows account, which belongs to the first tenant more often than not.
    /// </summary>
    public bool PickAccount { get; set; }
}
