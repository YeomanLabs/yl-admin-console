using AdminConsole.Core.Auth;

namespace AdminConsole.App.Services;

/// <summary>Plain-English text for every scope in docs/08-APP-REGISTRATION.md.</summary>
public static class ScopeCatalog
{
    private static readonly Dictionary<string, ScopeExplanation> All = new(StringComparer.OrdinalIgnoreCase)
    {
        [Scopes.OpenId] = new(Scopes.OpenId, "Sign you in", "Standard sign-in."),
        [Scopes.Profile] = new(Scopes.Profile, "See your name and email", "To show who is signed in."),
        [Scopes.OfflineAccess] = new(Scopes.OfflineAccess, "Stay signed in", "So you are not prompted every launch."),
        [Scopes.UserRead] = new(Scopes.UserRead, "Read your profile", "Standard sign-in."),
        [Scopes.UserReadAll] = new(Scopes.UserReadAll, "Read all users", "To build the local search index and the User 360 view."),
        [Scopes.GroupReadAll] = new(Scopes.GroupReadAll, "Read all groups and memberships", "For group badges and membership lists."),
        [Scopes.DeviceReadAll] = new(Scopes.DeviceReadAll, "Read Entra device objects", "To join devices to their owners."),
        [Scopes.OrganizationReadAll] = new(Scopes.OrganizationReadAll, "Read tenant details", "Name, domains, and whether Entra ID P1 is present."),
        [Scopes.ManagedDevicesReadAll] = new(Scopes.ManagedDevicesReadAll, "Read Intune devices", "Compliance, encryption and last check-in on the same screen as the user."),
        [Scopes.DeviceConfigurationReadAll] = new(Scopes.DeviceConfigurationReadAll, "Read Intune compliance policies", "Per-policy state on the device page."),
        [Scopes.AuthMethodReadAll] = new(Scopes.AuthMethodReadAll, "Read users' MFA methods", "To show what is registered."),
        [Scopes.AuditLogReadAll] = new(Scopes.AuditLogReadAll, "Read sign-in activity and audit logs", "The Activity tab (needs Entra ID P1)."),

        [Scopes.UserReadWriteAll] = new(Scopes.UserReadWriteAll, "Edit users", "Identity edits, disable/enable, create from template."),
        [Scopes.UserPasswordProfileReadWriteAll] = new(Scopes.UserPasswordProfileReadWriteAll, "Reset passwords", "The Reset password action."),
        [Scopes.UserRevokeSessionsAll] = new(Scopes.UserRevokeSessionsAll, "Revoke sign-in sessions", "The Revoke sessions action and offboarding."),
        [Scopes.GroupMemberReadWriteAll] = new(Scopes.GroupMemberReadWriteAll, "Change group membership", "Add/remove from groups."),
        [Scopes.GroupReadWriteAll] = new(Scopes.GroupReadWriteAll, "Edit groups", "Transfer Teams/M365 group ownership during offboarding."),
        [Scopes.AuthMethodReadWriteAll] = new(Scopes.AuthMethodReadWriteAll, "Remove MFA methods", "Delete a method, and the offboarding MFA step."),
        [Scopes.ManagedDevicesReadWriteAll] = new(Scopes.ManagedDevicesReadWriteAll, "Edit Intune devices", "Change the primary user."),
        [Scopes.ManagedDevicesPrivilegedOperationsAll] = new(Scopes.ManagedDevicesPrivilegedOperationsAll, "Run Intune device actions", "Sync, restart, retire, wipe."),

        [Scopes.BitLockerKeyReadAll] = new(Scopes.BitLockerKeyReadAll, "Reveal BitLocker recovery keys", "Only when you click Reveal; every reveal is logged."),
        [Scopes.DeviceLocalCredentialReadAll] = new(Scopes.DeviceLocalCredentialReadAll, "Reveal LAPS passwords", "Only when you click Reveal; every reveal is logged."),
    };

    public static ScopeExplanation For(string scope) =>
        All.TryGetValue(scope, out var e) ? e : new ScopeExplanation(scope, scope, string.Empty);

    /// <summary>The read tier shown on the first-run page, minus the three OpenID plumbing scopes.</summary>
    public static IReadOnlyList<ScopeExplanation> ReadTier =>
        Scopes.Read.Where(s => s is not (Scopes.OpenId or Scopes.Profile)).Select(For).ToList();
}
