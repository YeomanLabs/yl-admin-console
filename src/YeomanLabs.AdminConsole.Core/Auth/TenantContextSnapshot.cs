namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>Immutable payload applied to <see cref="TenantContext"/> in one step so observers never see a half-updated context.</summary>
public sealed record TenantContextSnapshot
{
    public string TenantId { get; init; } = string.Empty;
    public string TenantDisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> VerifiedDomains { get; init; } = [];
    public string? CountryLetterCode { get; init; }
    public bool OnPremisesSyncEnabled { get; init; }
    public DateTimeOffset? OnPremisesLastSyncDateTime { get; init; }
    public string SignedInUserId { get; init; } = string.Empty;
    public string SignedInUpn { get; init; } = string.Empty;
    public string SignedInDisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> GrantedScopes { get; init; } = [];
    public IReadOnlyList<DirectoryRoleRef> Roles { get; init; } = [];
    public bool HasP1 { get; init; }
    public bool HasP2 { get; init; }
}
