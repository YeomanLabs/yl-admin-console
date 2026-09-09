using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.Core.Tests.Fakes;

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

    public Task<AuthAccount> EnsureSignedInAsync(IReadOnlyList<string> scopes, CancellationToken ct) =>
        Task.FromResult(CurrentAccount!);

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

    public Task<bool> HasScopeAsync(string scope, CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<string>> GetGrantedScopesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Scopes.Read);

    public Task SignOutAsync(CancellationToken ct)
    {
        CurrentAccount = null;
        AccountChanged?.Invoke(this, null);
        return Task.CompletedTask;
    }
}
