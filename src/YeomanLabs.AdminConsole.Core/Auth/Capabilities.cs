namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>The Phase 1 write and sensitive capabilities, mapped to the scopes in docs/08-APP-REGISTRATION.md.</summary>
public static class Capabilities
{
    public static readonly Capability EditUser = new()
    {
        Id = "user.edit",
        Title = "Edit user properties",
        Scopes = [Scopes.UserReadWriteAll],
        RoleTemplateIds = [DirectoryRoleRef.UserAdministrator],
        RoleHint = "User Administrator",
    };

    public static readonly Capability CreateUser = new()
    {
        Id = "user.create",
        Title = "Create users from a template",
        Scopes = [Scopes.UserReadWriteAll],
        RoleTemplateIds = [DirectoryRoleRef.UserAdministrator],
        RoleHint = "User Administrator",
    };

    public static readonly Capability ResetPassword = new()
    {
        Id = "user.password",
        Title = "Reset passwords",
        Scopes = [Scopes.UserPasswordProfileReadWriteAll],
        RoleTemplateIds = [DirectoryRoleRef.UserAdministrator, DirectoryRoleRef.HelpdeskAdministrator, DirectoryRoleRef.PasswordAdministrator, DirectoryRoleRef.PrivilegedAuthenticationAdministrator],
        RoleHint = "User Administrator or Helpdesk Administrator",
    };

    public static readonly Capability RevokeSessions = new()
    {
        Id = "user.revokeSessions",
        Title = "Revoke sign-in sessions",
        Scopes = [Scopes.UserRevokeSessionsAll],
        RoleTemplateIds = [DirectoryRoleRef.UserAdministrator, DirectoryRoleRef.HelpdeskAdministrator],
        RoleHint = "User Administrator or Helpdesk Administrator",
    };

    public static readonly Capability AssignLicense = new()
    {
        Id = "license.assign",
        Title = "Assign and remove licenses",
        Scopes = [Scopes.UserReadWriteAll],
        RoleTemplateIds = [DirectoryRoleRef.LicenseAdministrator, DirectoryRoleRef.UserAdministrator],
        RoleHint = "License Administrator",
    };

    public static readonly Capability ChangeGroupMembership = new()
    {
        Id = "group.member",
        Title = "Add and remove group members",
        Scopes = [Scopes.GroupMemberReadWriteAll],
        RoleTemplateIds = [DirectoryRoleRef.GroupsAdministrator, DirectoryRoleRef.UserAdministrator],
        RoleHint = "Groups Administrator",
    };

    public static readonly Capability EditGroup = new()
    {
        Id = "group.edit",
        Title = "Edit groups and transfer ownership",
        Scopes = [Scopes.GroupReadWriteAll],
        RoleTemplateIds = [DirectoryRoleRef.GroupsAdministrator],
        RoleHint = "Groups Administrator",
    };

    public static readonly Capability DeleteAuthMethod = new()
    {
        Id = "auth.delete",
        Title = "Remove authentication methods",
        Scopes = [Scopes.AuthMethodReadWriteAll],
        RoleTemplateIds = [DirectoryRoleRef.AuthenticationAdministrator, DirectoryRoleRef.PrivilegedAuthenticationAdministrator],
        RoleHint = "Authentication Administrator",
    };

    public static readonly Capability EditDevice = new()
    {
        Id = "device.edit",
        Title = "Edit Intune devices",
        Scopes = [Scopes.ManagedDevicesReadWriteAll],
        RoleTemplateIds = [DirectoryRoleRef.IntuneAdministrator],
        RoleHint = "Intune Administrator",
    };

    public static readonly Capability DeviceActions = new()
    {
        Id = "device.actions",
        Title = "Run device actions (sync, restart, retire, wipe)",
        Scopes = [Scopes.ManagedDevicesPrivilegedOperationsAll],
        RoleTemplateIds = [DirectoryRoleRef.IntuneAdministrator],
        RoleHint = "Intune Administrator",
    };

    public static readonly Capability RevealBitLocker = new()
    {
        Id = "bitlocker.read",
        Title = "Reveal BitLocker recovery keys",
        Scopes = [Scopes.BitLockerKeyReadAll],
        RoleTemplateIds = [DirectoryRoleRef.CloudDeviceAdministrator],
        RoleHint = "Cloud Device Administrator",
        IsSensitive = true,
    };

    public static readonly Capability RevealLaps = new()
    {
        Id = "laps.read",
        Title = "Reveal LAPS passwords",
        Scopes = [Scopes.DeviceLocalCredentialReadAll],
        RoleTemplateIds = [DirectoryRoleRef.CloudDeviceAdministrator],
        RoleHint = "Cloud Device Administrator",
        IsSensitive = true,
    };

    /// <summary>Everything the Permissions page lists, in display order.</summary>
    public static readonly IReadOnlyList<Capability> All =
    [
        EditUser, CreateUser, ResetPassword, RevokeSessions, AssignLicense,
        ChangeGroupMembership, EditGroup, DeleteAuthMethod, EditDevice, DeviceActions,
        RevealBitLocker, RevealLaps,
    ];

    /// <summary>Every write-tier scope, for the one-shot consent prompt on the first write (doc 03 §Auth flow item 2).</summary>
    public static IReadOnlyList<string> WriteScopes => Scopes.Write;
}
