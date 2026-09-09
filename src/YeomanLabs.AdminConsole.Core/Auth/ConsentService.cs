using Microsoft.Extensions.Logging;

namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>
/// Incremental consent for one capability (doc 03 §Auth flow item 2): prompts only for what is missing, then refreshes
/// <see cref="TenantContext.GrantedScopes"/> so every grey rule in the UI re-evaluates at once.
/// </summary>
public sealed class ConsentService
{
    private readonly IAuthService _auth;
    private readonly TenantContext _tenant;
    private readonly PermissionChecker _checker;
    private readonly ILogger<ConsentService> _logger;

    public ConsentService(IAuthService auth, TenantContext tenant, PermissionChecker checker, ILogger<ConsentService> logger)
    {
        _auth = auth;
        _tenant = tenant;
        _checker = checker;
        _logger = logger;
    }

    /// <summary>
    /// Guarantees the capability's scopes are consented, prompting if needed. Returns the state afterwards; callers
    /// check <see cref="PermissionResult.CanRun"/> before writing. Cancelling the prompt leaves the state unchanged.
    /// </summary>
    public async Task<PermissionResult> EnsureAsync(Capability capability, CancellationToken ct)
    {
        var current = _checker.Check(capability);
        if (!current.NeedsConsent) return current;

        _logger.LogInformation("Capability {Capability} needs consent for {Scopes}", capability.Id, string.Join(", ", current.MissingScopes));
        var granted = await _auth.EnsureScopesAsync(capability.Scopes, ct).ConfigureAwait(false);
        _tenant.UpdateGrantedScopes(granted);
        return _checker.Check(capability);
    }

    /// <summary>
    /// Called before the first write of a session: consents to the whole write tier in one prompt rather than a prompt
    /// per action. Sensitive scopes stay out of it (doc 08).
    /// </summary>
    public async Task<IReadOnlyList<string>> EnsureWriteScopesAsync(CancellationToken ct)
    {
        var granted = await _auth.EnsureScopesAsync(Capabilities.WriteScopes, ct).ConfigureAwait(false);
        _tenant.UpdateGrantedScopes(granted);
        return granted;
    }
}
