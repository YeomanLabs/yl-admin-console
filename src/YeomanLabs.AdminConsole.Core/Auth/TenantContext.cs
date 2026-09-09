namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>
/// What everything downstream needs to know about the current tenant and admin (doc 03 §Auth flow, item 3).
/// A class, not a static: Phase 3 makes it a per-tab instance. Populated by <see cref="TenantContextLoader"/>.
/// </summary>
public sealed class TenantContext
{
    public const string AadPremiumP1PlanId = "41781fb2-bc02-4b7c-bd55-b576c07bb09d";
    public const string AadPremiumP2PlanId = "eec0eb4f-6444-4f95-aba0-50c24d67f998";

    public bool IsLoaded { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public string TenantDisplayName { get; private set; } = string.Empty;
    public IReadOnlyList<string> VerifiedDomains { get; private set; } = [];
    public string? CountryLetterCode { get; private set; }
    public bool OnPremisesSyncEnabled { get; private set; }
    public DateTimeOffset? OnPremisesLastSyncDateTime { get; private set; }

    public string SignedInUserId { get; private set; } = string.Empty;
    public string SignedInUpn { get; private set; } = string.Empty;
    public string SignedInDisplayName { get; private set; } = string.Empty;

    public IReadOnlyList<string> GrantedScopes { get; private set; } = [];
    public IReadOnlyList<DirectoryRoleRef> Roles { get; private set; } = [];

    /// <summary>Entra ID P1 (or P2, which includes P1) is enabled — gates signInActivity and sign-in logs.</summary>
    public bool HasP1 { get; private set; }
    public bool HasP2 { get; private set; }

    /// <summary>Raised after <see cref="Update"/>; may fire on any thread.</summary>
    public event EventHandler? Changed;

    public bool HasScope(string scope) => GrantedScopes.Any(s => Scopes.Matches(s, scope));

    public bool HasRole(string roleTemplateId) =>
        Roles.Any(r => string.Equals(r.RoleTemplateId, roleTemplateId, StringComparison.OrdinalIgnoreCase));

    public bool IsGlobalAdmin => HasRole(DirectoryRoleRef.GlobalAdministrator);

    public void Update(TenantContextSnapshot snapshot)
    {
        TenantId = snapshot.TenantId;
        TenantDisplayName = snapshot.TenantDisplayName;
        VerifiedDomains = snapshot.VerifiedDomains;
        CountryLetterCode = snapshot.CountryLetterCode;
        OnPremisesSyncEnabled = snapshot.OnPremisesSyncEnabled;
        OnPremisesLastSyncDateTime = snapshot.OnPremisesLastSyncDateTime;
        SignedInUserId = snapshot.SignedInUserId;
        SignedInUpn = snapshot.SignedInUpn;
        SignedInDisplayName = snapshot.SignedInDisplayName;
        GrantedScopes = snapshot.GrantedScopes;
        Roles = snapshot.Roles;
        HasP1 = snapshot.HasP1;
        HasP2 = snapshot.HasP2;
        IsLoaded = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        Update(new TenantContextSnapshot());
        IsLoaded = false;
    }
}
