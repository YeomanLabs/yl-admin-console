namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>An activated directory role held by the signed-in admin. Compare on <see cref="RoleTemplateId"/>, never on the display name.</summary>
public sealed record DirectoryRoleRef(string DisplayName, string RoleTemplateId)
{
    // Well-known role template IDs used by the grey rule (UX §6). Stable across tenants.
    public const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    public const string GlobalReader = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";
    public const string UserAdministrator = "fe930be7-5e62-47db-91af-98c3a49a38b1";
    public const string HelpdeskAdministrator = "729827e3-9c14-49f7-bb1b-9608f156bbb8";
    public const string PasswordAdministrator = "966707d0-3269-4727-9be2-8c3a10f19b9d";
    public const string GroupsAdministrator = "fdd7a751-b60b-444a-984c-02652fe8fa1c";
    public const string IntuneAdministrator = "3a2c62db-5318-420d-8d74-23affee5d9d5";
    public const string LicenseAdministrator = "4d6ac14f-3453-41d0-bef9-a3e0c569773a";
    public const string CloudDeviceAdministrator = "7698a772-787b-4ac8-901f-60d6b08affd2";
    public const string AuthenticationAdministrator = "c4e39bd9-1100-46d3-8c65-fb160da0071f";
    public const string PrivilegedAuthenticationAdministrator = "7be44c8a-adaf-4e2a-84d6-ab2649e08a13";
    public const string PrivilegedRoleAdministrator = "e8611ab8-c189-46e8-94e1-60213ab1f814";
    public const string SecurityAdministrator = "194ae4cb-b126-40b2-bd5b-6091b380977d";
    public const string SecurityReader = "5d6b6bb7-de71-4623-b4af-96380a352509";
    public const string ReportsReader = "4a5d8f65-41da-4de4-8968-e035b65339cf";
    public const string DirectoryReaders = "88d8e3e3-8f55-4a1e-953a-9b9898b8876b";
    public const string ExchangeAdministrator = "29232cdf-9323-42fd-ade2-1d097af3e4de";
    public const string SharePointAdministrator = "f28a1f50-f6e7-4571-818b-6a12f2af6b6c";
    public const string TeamsAdministrator = "69091246-20e8-4a56-aa4d-066075b2a7a8";
    public const string ApplicationAdministrator = "9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3";
    public const string CloudApplicationAdministrator = "158c047a-c907-4556-b7ef-446551a6b5f7";

    /// <summary>
    /// Local names for template IDs. Graph blanks <c>displayName</c> on <c>directoryRole</c> objects unless the
    /// token holds <c>Directory.Read.All</c> or <c>RoleManagement.Read.Directory</c>, neither of which we request
    /// (DECISIONS 2026-09-09), so role hints in the UI come from this table.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [GlobalAdministrator] = "Global Administrator",
        [GlobalReader] = "Global Reader",
        [UserAdministrator] = "User Administrator",
        [HelpdeskAdministrator] = "Helpdesk Administrator",
        [PasswordAdministrator] = "Password Administrator",
        [GroupsAdministrator] = "Groups Administrator",
        [IntuneAdministrator] = "Intune Administrator",
        [LicenseAdministrator] = "License Administrator",
        [CloudDeviceAdministrator] = "Cloud Device Administrator",
        [AuthenticationAdministrator] = "Authentication Administrator",
        [PrivilegedAuthenticationAdministrator] = "Privileged Authentication Administrator",
        [PrivilegedRoleAdministrator] = "Privileged Role Administrator",
        [SecurityAdministrator] = "Security Administrator",
        [SecurityReader] = "Security Reader",
        [ReportsReader] = "Reports Reader",
        [DirectoryReaders] = "Directory Readers",
        [ExchangeAdministrator] = "Exchange Administrator",
        [SharePointAdministrator] = "SharePoint Administrator",
        [TeamsAdministrator] = "Teams Administrator",
        [ApplicationAdministrator] = "Application Administrator",
        [CloudApplicationAdministrator] = "Cloud Application Administrator",
    };

    /// <summary>Friendly name for a template ID, or the ID itself when unknown.</summary>
    public static string DisplayNameFor(string roleTemplateId) =>
        KnownNames.TryGetValue(roleTemplateId, out var name) ? name : roleTemplateId;

    /// <summary>Builds a ref, preferring Graph's name when present and falling back to the local table.</summary>
    public static DirectoryRoleRef From(string? displayName, string roleTemplateId) =>
        new(string.IsNullOrWhiteSpace(displayName) ? DisplayNameFor(roleTemplateId) : displayName, roleTemplateId);
}
