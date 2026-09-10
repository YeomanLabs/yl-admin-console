using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Tests.Fakes;

/// <summary>Hands out a fixed token and counts refreshes. Never interactive.</summary>
public sealed class FakeAuthService : IAuthService
{
    public string Token { get; set; } = "token-1";
    public int TokenRequests { get; private set; }
    public int ForceRefreshes { get; private set; }

    public AuthAccount? CurrentAccount { get; set; } = new()
    {
        Username = "admin@contoso.com",
        TenantId = "00000000-0000-0000-0000-000000000001",
        HomeAccountId = "home",
    };

    public event EventHandler<AuthAccount?>? AccountChanged;

    /// <summary>What <see cref="GetGrantedScopesAsync"/> and <see cref="HasScopeAsync"/> report. Tests mutate this directly.</summary>
    public List<string> GrantedScopes { get; } = [.. Scopes.Read];

    /// <summary>Scopes the next <see cref="EnsureScopesAsync"/> will add, simulating the admin accepting consent.</summary>
    public bool ConsentSucceeds { get; set; } = true;

    public int ConsentPrompts { get; private set; }

    public Task<AuthAccount> EnsureSignedInAsync(IReadOnlyList<string> scopes, CancellationToken ct) =>
        Task.FromResult(CurrentAccount!);

    public Task<IReadOnlyList<string>> EnsureScopesAsync(IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var missing = scopes.Where(s => !GrantedScopes.Any(g => Scopes.Matches(g, s))).ToList();
        if (missing.Count == 0) return Task.FromResult<IReadOnlyList<string>>(GrantedScopes);

        ConsentPrompts++;
        if (!ConsentSucceeds)
        {
            throw new SignInException(SignInFailureKind.ConsentDeclined, "Consent was declined.", "access_denied", null, null);
        }

        GrantedScopes.AddRange(missing);
        return Task.FromResult<IReadOnlyList<string>>(GrantedScopes);
    }

    public Task<string> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken ct, bool forceRefresh = false)
    {
        TokenRequests++;
        if (forceRefresh)
        {
            ForceRefreshes++;
            Token = $"token-{ForceRefreshes + 1}";
        }

        return Task.FromResult(Token);
    }

    public Task<bool> HasScopeAsync(string scope, CancellationToken ct) =>
        Task.FromResult(GrantedScopes.Any(g => Scopes.Matches(g, scope)));

    public Task<IReadOnlyList<string>> GetGrantedScopesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(GrantedScopes);

    public Task SignOutAsync(CancellationToken ct)
    {
        CurrentAccount = null;
        AccountChanged?.Invoke(this, null);
        return Task.CompletedTask;
    }
}
