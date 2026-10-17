namespace AdminConsole.Core.Auth;

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

    // Exchange Online (Phase 3, doc 13). A different resource from Graph, so it never appears in a Graph token's `scp`
    // and is asked for with its resource prefix.
    public const string ExchangeManage = "https://outlook.office365.com/Exchange.Manage";

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

    /// <summary>The Exchange Online tier (doc 13). Its own resource, so its own consent prompt and its own token.</summary>
    public static readonly IReadOnlyList<string> Exchange =
    [
        ExchangeManage,
    ];

    /// <summary>Graph scopes are matched case-insensitively by the service; do the same locally.</summary>
    public static bool Matches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The resource a scope belongs to, as the prefix MSAL needs (<c>https://outlook.office365.com/</c>), or empty for a
    /// bare name, which MSAL treats as Microsoft Graph. One interactive request may only name one resource.
    /// </summary>
    public static string ResourceOf(string scope) =>
        Uri.TryCreate(scope, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? $"{uri.Scheme}://{uri.Host}/"
            : string.Empty;

    /// <summary>The name a token's <c>scp</c> claim and an app registration use: <c>Exchange.Manage</c>, not the URI.</summary>
    public static string ShortName(string scope)
    {
        var resource = ResourceOf(scope);
        return resource.Length == 0 ? scope : scope[resource.Length..];
    }
}
