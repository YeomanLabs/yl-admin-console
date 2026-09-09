namespace YeomanLabs.AdminConsole.Core.Auth;

public interface IAuthService
{
    /// <summary>Null until <see cref="EnsureSignedInAsync"/> has succeeded at least once this process.</summary>
    AuthAccount? CurrentAccount { get; }

    /// <summary>Raised when the signed-in account changes, including to null on sign-out. May fire on any thread.</summary>
    event EventHandler<AuthAccount?>? AccountChanged;

    /// <summary>Silent first; interactive (WAM broker) on <c>MsalUiRequiredException</c>. Safe to call repeatedly.</summary>
    Task<AuthAccount> EnsureSignedInAsync(IReadOnlyList<string> scopes, CancellationToken ct);

    /// <summary>
    /// Silent only. Throws <see cref="SignInRequiredException"/> if interaction would be needed.
    /// <paramref name="forceRefresh"/> bypasses the cached access token (used once after a 401).
    /// </summary>
    Task<string> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken ct, bool forceRefresh = false);

    /// <summary>True if the cached access token's <c>scp</c> claim contains <paramref name="scope"/>. Never interactive.</summary>
    Task<bool> HasScopeAsync(string scope, CancellationToken ct);

    /// <summary>All scopes in the cached access token's <c>scp</c> claim; empty when not signed in. Never interactive.</summary>
    Task<IReadOnlyList<string>> GetGrantedScopesAsync(CancellationToken ct);

    Task SignOutAsync(CancellationToken ct);
}
