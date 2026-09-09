namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>An activated directory role held by the signed-in admin. Compare on <see cref="RoleTemplateId"/>, never on the display name.</summary>
public sealed record DirectoryRoleRef(string DisplayName, string RoleTemplateId)
{
    // Well-known role template IDs used by the grey rule (UX §6). Stable across tenants.
    public const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    public const string UserAdministrator = "fe930be7-5e62-47db-91af-98c3a49a38b1";
    public const string HelpdeskAdministrator = "729827e3-9c14-49f7-bb1b-9608f156bbb8";
    public const string GroupsAdministrator = "fdd7a751-b60b-444a-984c-02652fe8fa1c";
    public const string IntuneAdministrator = "3a2c62db-5318-420d-8d74-23affee5d9d5";
    public const string LicenseAdministrator = "4d6ac14f-3453-41d0-bef9-a3e0c569773a";
    public const string CloudDeviceAdministrator = "7698a772-787b-4ac8-901f-60d6b08affd2";
    public const string AuthenticationAdministrator = "c4e39bd9-1100-46d3-8c65-fb160da0071f";
    public const string PrivilegedAuthenticationAdministrator = "7be44c8a-adaf-4e2a-84d6-ab2649e08a13";
    public const string PrivilegedRoleAdministrator = "e8611ab8-c189-46e8-94e1-60213ab1f814";
}
