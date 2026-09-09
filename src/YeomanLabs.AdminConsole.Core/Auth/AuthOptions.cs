namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>Bound from the <c>Auth</c> section of appsettings.json. Public client: no secret.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Application (client) ID of the multi-tenant public client registration (doc 08).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Authority URL. Default is the multi-tenant "organizations" endpoint.</summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com/organizations";
}
