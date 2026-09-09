namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>
/// Answers "can the signed-in admin do this, and if not why" from <see cref="TenantContext"/> (UX §6). Pure and
/// synchronous so ViewModels can call it during binding; consent itself goes through <see cref="IAuthService"/>.
/// </summary>
public sealed class PermissionChecker
{
    private readonly TenantContext _tenant;

    public PermissionChecker(TenantContext tenant) => _tenant = tenant;

    public PermissionResult Check(Capability capability)
    {
        var missing = capability.Scopes.Where(s => !_tenant.HasScope(s)).ToList();
        if (missing.Count > 0)
        {
            return new PermissionResult
            {
                Capability = capability,
                State = PermissionState.NeedsConsent,
                MissingScopes = missing,
                Reason = $"Needs the '{missing[0]}' permission — click to grant",
            };
        }

        // Roles are necessary but not enumerable with certainty; a Global Administrator always passes.
        var hasRole = capability.RoleTemplateIds.Count == 0
                      || _tenant.IsGlobalAdmin
                      || capability.RoleTemplateIds.Any(_tenant.HasRole);
        if (hasRole)
        {
            return new PermissionResult { Capability = capability, State = PermissionState.Allowed };
        }

        return new PermissionResult
        {
            Capability = capability,
            State = PermissionState.AllowedWithRoleWarning,
            Reason = $"Your account may lack a role that allows this ({capability.RoleHint})",
        };
    }

    /// <summary>The UX §6 caption for a 403 that came back after consent was granted.</summary>
    public static string ForbiddenReason(Capability capability) =>
        capability.RoleHint is null
            ? "Your account lacks a role that allows this."
            : $"Your account lacks a role that allows this ({capability.RoleHint}).";
}
