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

    // OneDrive (M16, doc 14). SharePoint is addressed by its application id rather than by a host: a token for
    // `00000003-0000-0ff1-ce00-000000000000` is accepted on the tenant's admin, OneDrive and root hosts alike (proven
    // 2026-10-17), so one scope serves every tenant without knowing its SharePoint name in advance.
    public const string SharePointResource = "00000003-0000-0ff1-ce00-000000000000/";
    public const string SharePointFullControl = SharePointResource + "AllSites.FullControl";

    /// <summary>Graph: read the files of a drive the admin can open — what a OneDrive export downloads with.</summary>
    public const string FilesReadAll = "Files.Read.All";

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

    /// <summary>The SharePoint tier (M16): site properties, site admins and lock state, through the SharePoint admin API.</summary>
    public static readonly IReadOnlyList<string> SharePoint =
    [
        SharePointFullControl,
    ];

    /// <summary>The files tier (M16): asked for the first time an export runs, not before.</summary>
    public static readonly IReadOnlyList<string> Files =
    [
        FilesReadAll,
    ];

    /// <summary>Graph scopes are matched case-insensitively by the service; do the same locally.</summary>
    public static bool Matches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The resource a scope belongs to, as the prefix MSAL needs (<c>https://outlook.office365.com/</c>), or empty for a
    /// bare name, which MSAL treats as Microsoft Graph. One interactive request may only name one resource.
    /// </summary>
    public static string ResourceOf(string scope)
    {
        if (Uri.TryCreate(scope, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) return $"{uri.Scheme}://{uri.Host}/";

        // `{application id}/Scope`, the form SharePoint is asked for by.
        var slash = scope.IndexOf('/');
        return slash > 0 && Guid.TryParse(scope[..slash], out _) ? scope[..(slash + 1)] : string.Empty;
    }

    /// <summary>The name a token's <c>scp</c> claim and an app registration use: <c>Exchange.Manage</c>, not the URI.</summary>
    public static string ShortName(string scope)
    {
        var resource = ResourceOf(scope);
        return resource.Length == 0 ? scope : scope[resource.Length..];
    }
}
