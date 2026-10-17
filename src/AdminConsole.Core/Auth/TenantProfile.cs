using System.Text.Json.Serialization;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Auth;

/// <summary>
/// One tenant the console signs in to (M20.1): which registration, which directory, which account last used it, and
/// its on-prem domains. <c>tenant.json</c> holds a list of these.
/// </summary>
public sealed record TenantProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>What the switcher and the tab badge say. The tenant's display name once it has been signed in to.</summary>
    public string Name { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    /// <summary>The authority the registration was set up with; <see cref="EffectiveAuthority"/> pins it to <see cref="TenantId"/>.</summary>
    public string Authority { get; init; } = "https://login.microsoftonline.com/organizations";

    /// <summary>
    /// The directory, once known (or typed: an id or a verified domain). Tokens are asked of this tenant's own
    /// authority, so an account that can reach several tenants (a partner admin) is never handed the wrong one.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>The MSAL home account last signed in with here, so each tenant reopens with its own account.</summary>
    public string? AccountId { get; init; }

    public string? Username { get; init; }

    /// <summary>What the setup screen was told, kept for Settings (M10.1).</summary>
    public string? TenantHint { get; init; }

    /// <summary>
    /// On-prem domains (M20.3). Never a password: a profile's domains bind as the signed-in Windows user. The first
    /// profile also inherits the <c>OnPrem</c> section of appsettings, which is where a lab credential lives.
    /// </summary>
    public IReadOnlyList<TenantDomain> Domains { get; init; } = [];

    [JsonIgnore]
    public bool IsConfigured => TenantConfig.LooksLikeClientId(ClientId);

    [JsonIgnore]
    public string EffectiveAuthority => TenantId is { Length: > 0 } t ? $"https://login.microsoftonline.com/{t}" : Authority;

    [JsonIgnore]
    public string DisplayName => Name.Length > 0 ? Name : TenantHint ?? TenantId ?? "Tenant";

    public TenantConfig ToConfig() => new() { ClientId = ClientId, Authority = Authority, TenantHint = TenantHint };
}
