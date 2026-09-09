namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>
/// Which app registration this installation signs in with (M10.1).
/// </summary>
/// <remarks>
/// This is per-installation, not per-build, so it lives beside <c>settings.json</c> in <c>%LOCALAPPDATA%</c> rather
/// than in the shipped <c>appsettings.json</c> — for the same reasons the M8.1 settings moved there. An update
/// overwrites anything in the install directory, and that directory may be read-only under Program Files. The shipped
/// file stays as a default for a build that already knows its tenant; whatever is here wins.
/// </remarks>
public sealed record TenantConfig
{
    /// <summary>Application (client) ID of the registration created by <c>scripts/New-AppRegistration.ps1</c>.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Authority. The default is the multi-tenant <c>organizations</c> endpoint, which works for any tenant. A
    /// single-tenant registration needs its tenant id here instead, which the script writes when asked for one.
    /// </summary>
    public string Authority { get; init; } = "https://login.microsoftonline.com/organizations";

    /// <summary>What the setup screen showed when this was saved, so Settings can say which tenant it was made for.</summary>
    public string? TenantHint { get; init; }

    public bool IsConfigured => LooksLikeClientId(ClientId);

    /// <summary>
    /// A client ID is a GUID. Checking the shape locally turns "nothing happens when I sign in" into a message at the
    /// point the value is typed, which is the only place anyone can do anything about it.
    /// </summary>
    public static bool LooksLikeClientId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value.Trim(), out var g) && g != Guid.Empty;
}
