using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace AdminConsole.Core.Auth;

/// <summary>
/// MSAL public client with the Windows WAM broker and a DPAPI-protected file token cache
/// (docs/03-ARCHITECTURE.md §Auth flow). Delegated permissions only; no secrets.
/// </summary>
public sealed class AuthService : IAuthService
{
    private const string ClientName = "Admin Console";

    private readonly AuthOptions _options;
    private readonly Func<IntPtr> _parentWindow;
    private readonly ILogger<AuthService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _interactiveLock = new(1, 1);

    private IPublicClientApplication? _pca;
    private IAccount? _account;

    public AuthService(AuthOptions options, Func<IntPtr> parentWindow, ILogger<AuthService> logger)
    {
        _options = options;
        _parentWindow = parentWindow;
        _logger = logger;
    }

    public AuthAccount? CurrentAccount { get; private set; }

    public event EventHandler<AuthAccount?>? AccountChanged;

    public async Task<AuthAccount> EnsureSignedInAsync(IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var pca = await GetPcaAsync(ct).ConfigureAwait(false);
        var account = await ResolveAccountAsync(pca, ct).ConfigureAwait(false);

        AuthenticationResult result;
        try
        {
            result = await pca.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException ex)
        {
            _logger.LogInformation("Silent token acquisition needs interaction ({ErrorCode}); prompting", ex.ErrorCode);
            await _interactiveLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var builder = pca.AcquireTokenInteractive(scopes);
                builder = account is not null
                    ? builder.WithAccount(account)
                    : _options.PickAccount
                        ? builder.WithPrompt(Prompt.SelectAccount)
                        : builder.WithAccount(PublicClientApplication.OperatingSystemAccount);
                result = await builder.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (MsalException msal)
            {
                var mapped = Map(msal);
                _logger.LogWarning("Interactive sign-in failed: {Kind} ({ErrorCode})", mapped.Kind, mapped.ErrorCode);
                throw mapped;
            }
            finally
            {
                _interactiveLock.Release();
            }
        }

        return SetAccount(result.Account);
    }

    public async Task<IReadOnlyList<string>> EnsureScopesAsync(IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var granted = await GetGrantedScopesAsync(ct).ConfigureAwait(false);
        var missing = scopes.Where(s => !granted.Any(g => Scopes.Matches(g, s))).ToList();
        if (missing.Count == 0) return granted;

        _logger.LogInformation("Requesting consent for {Count} additional scope(s)", missing.Count);
        var pca = await GetPcaAsync(ct).ConfigureAwait(false);
        var account = await ResolveAccountAsync(pca, ct).ConfigureAwait(false);

        // One interactive request may name only one resource, so Graph and Exchange scopes are asked for separately.
        foreach (var resource in missing.GroupBy(Scopes.ResourceOf))
        {
            await _interactiveLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var builder = pca.AcquireTokenInteractive(resource.ToList());
                builder = account is not null
                    ? builder.WithAccount(account)
                    : builder.WithAccount(PublicClientApplication.OperatingSystemAccount);
                var result = await builder.ExecuteAsync(ct).ConfigureAwait(false);
                SetAccount(result.Account);
                account = result.Account;
            }
            catch (MsalException msal)
            {
                var mapped = Map(msal);
                _logger.LogWarning("Consent failed: {Kind} ({ErrorCode})", mapped.Kind, mapped.ErrorCode);
                throw mapped;
            }
            finally
            {
                _interactiveLock.Release();
            }
        }

        // The pre-consent access token is still cached and would satisfy a silent request for a subset of scopes,
        // so force a refresh before reading `scp` back — otherwise the new grants look absent.
        await GetTokenAsync([Scopes.UserRead], ct, forceRefresh: true).ConfigureAwait(false);
        var now = await GetGrantedScopesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Consent complete; {Count} scope(s) granted", now.Count);
        return now;
    }

    /// <summary>MSAL → the UX §9 failure states. Kept here so the App never depends on MSAL error codes.</summary>
    private static SignInException Map(MsalException ex)
    {
        var code = ex.ErrorCode ?? string.Empty;
        var text = ex.Message ?? string.Empty;

        if (ex is MsalClientException && (code.Contains("cancel", StringComparison.OrdinalIgnoreCase) || code == "user_canceled" || code == "authentication_canceled"))
        {
            return new SignInException(SignInFailureKind.Cancelled, "Sign-in was cancelled.", code, text, ex);
        }

        // Conditional Access blocks arrive as AADSTS5xxxx (53000–53011 device/location/compliance) on a service exception.
        if (text.Contains("AADSTS530", StringComparison.OrdinalIgnoreCase) || text.Contains("AADSTS5300", StringComparison.OrdinalIgnoreCase) ||
            (ex is MsalUiRequiredException ui && ui.Classification == UiRequiredExceptionClassification.AdditionalAction))
        {
            return new SignInException(SignInFailureKind.ConditionalAccess, text, code, text, ex);
        }

        if (code == "access_denied" || text.Contains("AADSTS65004", StringComparison.OrdinalIgnoreCase) ||
            (ex is MsalUiRequiredException ui2 && ui2.Classification == UiRequiredExceptionClassification.ConsentRequired))
        {
            return new SignInException(SignInFailureKind.ConsentDeclined, "Consent was declined. The app cannot work without read access to your tenant.", code, text, ex);
        }

        return new SignInException(SignInFailureKind.Other, $"Sign-in failed ({code}).", code, text, ex);
    }

    public async Task<string> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken ct, bool forceRefresh = false)
    {
        var pca = await GetPcaAsync(ct).ConfigureAwait(false);
        var account = await ResolveAccountAsync(pca, ct).ConfigureAwait(false)
            ?? throw new SignInRequiredException("No signed-in account.");
        try
        {
            var result = await pca.AcquireTokenSilent(scopes, account)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
            SetAccount(result.Account);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new SignInRequiredException($"Sign-in required ({ex.ErrorCode}).", ex);
        }
    }

    public async Task<bool> HasScopeAsync(string scope, CancellationToken ct)
    {
        var granted = await GetGrantedScopesAsync(ct).ConfigureAwait(false);
        return granted.Any(s => Scopes.Matches(s, scope));
    }

    public async Task<IReadOnlyList<string>> GetGrantedScopesAsync(CancellationToken ct)
    {
        // A Graph access token carries every scope the user has consented to for this app in `scp`,
        // regardless of which subset was requested, so asking for the smallest scope is enough.
        IReadOnlyList<string> graph;
        try
        {
            var token = await GetTokenAsync([Scopes.UserRead], ct).ConfigureAwait(false);
            graph = AccessTokenClaims.GetScopes(token);
        }
        catch (SignInRequiredException)
        {
            return [];
        }

        // A Graph token knows nothing about another resource, so Exchange is asked for separately and silently. No
        // token means not granted, never an error: an install whose registration predates Phase 3 is a normal state.
        return
        [
            .. graph,
            .. await GetResourceScopesAsync(Scopes.ExchangeManage, ct).ConfigureAwait(false),
            .. await GetResourceScopesAsync(Scopes.SharePointFullControl, ct).ConfigureAwait(false),
        ];
    }

    /// <summary>
    /// The scopes granted on another resource, written as full URIs (<c>https://outlook.office365.com/Exchange.Manage</c>)
    /// so they can never be mistaken for a Graph scope of the same name.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetResourceScopesAsync(string probeScope, CancellationToken ct)
    {
        var resource = Scopes.ResourceOf(probeScope);
        try
        {
            var token = await GetTokenAsync([probeScope], ct).ConfigureAwait(false);
            return [.. AccessTokenClaims.GetScopes(token).Select(s => resource + s)];
        }
        catch (SignInRequiredException)
        {
            return [];
        }
        catch (MsalException ex)
        {
            _logger.LogInformation("No token for {Resource} ({ErrorCode}); treating as not granted", resource, ex.ErrorCode);
            return [];
        }
    }

    /// <summary>
    /// Signs this tenant's account out. Only this one: since M20 the token cache can hold the accounts of other tenants
    /// open in other tabs, and signing out of one must not sign the admin out of all of them.
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct)
    {
        var pca = await GetPcaAsync(ct).ConfigureAwait(false);
        var account = await ResolveAccountAsync(pca, ct).ConfigureAwait(false);
        if (account is not null) await pca.RemoveAsync(account).ConfigureAwait(false);

        _account = null;
        if (CurrentAccount is not null)
        {
            CurrentAccount = null;
            _logger.LogInformation("Signed out");
            AccountChanged?.Invoke(this, null);
        }
    }

    private AuthAccount SetAccount(IAccount account)
    {
        _account = account;
        var mapped = new AuthAccount
        {
            Username = account.Username,
            TenantId = account.HomeAccountId?.TenantId ?? string.Empty,
            HomeAccountId = account.HomeAccountId?.Identifier ?? string.Empty,
        };
        if (mapped != CurrentAccount)
        {
            CurrentAccount = mapped;
            _logger.LogInformation("Signed in to tenant {TenantId}", mapped.TenantId);
            AccountChanged?.Invoke(this, mapped);
        }

        return mapped;
    }

    private async Task<IAccount?> ResolveAccountAsync(IPublicClientApplication pca, CancellationToken ct)
    {
        if (_account is not null) return _account;
        ct.ThrowIfCancellationRequested();
        var accounts = await pca.GetAccountsAsync().ConfigureAwait(false);
        _account = ChooseAccount(accounts, _options.AccountId, _options.TenantId, _options.PickAccount);
        return _account;
    }

    /// <summary>
    /// Which cached account this tenant uses (M20): the one it was last opened with; else one homed in this tenant;
    /// else, for a profile that has never been signed in to, none (so the prompt asks) — or, for the single-tenant case
    /// that predates profiles, the only account there is.
    /// </summary>
    internal static IAccount? ChooseAccount(IEnumerable<IAccount> accounts, string? accountId, string? tenantId, bool pickAccount)
    {
        var list = accounts.ToList();
        if (accountId is { Length: > 0 } && list.FirstOrDefault(a => a.HomeAccountId?.Identifier == accountId) is { } byId) return byId;
        if (tenantId is { Length: > 0 } && list.FirstOrDefault(a => string.Equals(a.HomeAccountId?.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)) is { } byTenant)
        {
            return byTenant;
        }

        // A partner admin's account is homed in their own tenant, not the customer's: with a known account id it was
        // found above; without one, a second profile must ask rather than guess.
        return pickAccount ? null : list.FirstOrDefault();
    }

    private async Task<IPublicClientApplication> GetPcaAsync(CancellationToken ct)
    {
        if (_pca is not null) return _pca;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pca is not null) return _pca;
            if (string.IsNullOrWhiteSpace(_options.ClientId))
            {
                throw new InvalidOperationException("Auth:ClientId is not configured (see docs/08-APP-REGISTRATION.md).");
            }

            AppPaths.EnsureCreated();

            var pca = PublicClientApplicationBuilder.Create(_options.ClientId)
                .WithAuthority(new Uri(_options.Authority))
                .WithRedirectUri($"ms-appx-web://microsoft.aad.brokerplugin/{_options.ClientId}")
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = ClientName })
                .WithParentActivityOrWindow(_parentWindow)
                .WithClientName(ClientName)
                .WithClientVersion(typeof(AuthService).Assembly.GetName().Version?.ToString() ?? "0.0")
                .Build();

            var storage = new StorageCreationPropertiesBuilder(AppPaths.MsalCacheFileName, AppPaths.Root).Build();
            var cacheHelper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
            cacheHelper.RegisterCache(pca.UserTokenCache);

            _pca = pca;
            return pca;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
