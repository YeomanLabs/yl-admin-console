namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>
/// Delegated Microsoft Graph scopes in the three consent tiers from docs/08-APP-REGISTRATION.md.
/// Casing matches what Graph returns in the <c>scp</c> claim so string comparisons never surprise us
/// (note <c>BitlockerKey</c>, lowercase L, as issued by the service).
/// </summary>
public static class Scopes
{
    // Read tier — requested at first run.
    public const string OpenId = "openid";
    public const string Profile = "profile";
    public const string OfflineAccess = "offline_access";
    public const string UserRead = "User.Read";
    public const string UserReadAll = "User.Read.All";
    public const string GroupReadAll = "Group.Read.All";
    public const string DeviceReadAll = "Device.Read.All";
    public const string OrganizationReadAll = "Organization.Read.All";
    public const string ManagedDevicesReadAll = "DeviceManagementManagedDevices.Read.All";
    public const string DeviceConfigurationReadAll = "DeviceManagementConfiguration.Read.All";
    public const string AuthMethodReadAll = "UserAuthenticationMethod.Read.All";
    public const string AuditLogReadAll = "AuditLog.Read.All";

    // Write tier — incremental consent on first write.
    public const string UserReadWriteAll = "User.ReadWrite.All";
    public const string UserPasswordProfileReadWriteAll = "User-PasswordProfile.ReadWrite.All";
    public const string UserRevokeSessionsAll = "User.RevokeSessions.All";
    public const string GroupMemberReadWriteAll = "GroupMember.ReadWrite.All";
    public const string GroupReadWriteAll = "Group.ReadWrite.All";
    public const string AuthMethodReadWriteAll = "UserAuthenticationMethod.ReadWrite.All";
    public const string ManagedDevicesReadWriteAll = "DeviceManagementManagedDevices.ReadWrite.All";
    public const string ManagedDevicesPrivilegedOperationsAll = "DeviceManagementManagedDevices.PrivilegedOperations.All";

    // Sensitive tier — requested only when a reveal button is clicked.
    public const string BitLockerKeyReadAll = "BitlockerKey.Read.All";
    public const string DeviceLocalCredentialReadAll = "DeviceLocalCredential.Read.All";

    public static readonly IReadOnlyList<string> Read =
    [
        OpenId, Profile, OfflineAccess,
        UserRead, UserReadAll, GroupReadAll, DeviceReadAll, OrganizationReadAll,
        ManagedDevicesReadAll, DeviceConfigurationReadAll, AuthMethodReadAll, AuditLogReadAll,
    ];

    public static readonly IReadOnlyList<string> Write =
    [
        UserReadWriteAll, UserPasswordProfileReadWriteAll, UserRevokeSessionsAll,
        GroupMemberReadWriteAll, GroupReadWriteAll, AuthMethodReadWriteAll,
        ManagedDevicesReadWriteAll, ManagedDevicesPrivilegedOperationsAll,
    ];

    public static readonly IReadOnlyList<string> Sensitive =
    [
        BitLockerKeyReadAll, DeviceLocalCredentialReadAll,
    ];

    /// <summary>Graph scopes are matched case-insensitively by the service; do the same locally.</summary>
    public static bool Matches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
