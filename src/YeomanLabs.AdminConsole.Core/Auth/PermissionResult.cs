namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>Why a capability is or is not available right now (UX §6 "Explain the grey").</summary>
public enum PermissionState
{
    /// <summary>Scopes are consented and the admin holds a role that usually allows it.</summary>
    Allowed,

    /// <summary>Scopes are consented but no known-good role was found. Allowed to try; Graph decides.</summary>
    AllowedWithRoleWarning,

    /// <summary>One or more delegated scopes are not consented yet. Clicking grants them.</summary>
    NeedsConsent,
}

/// <summary>The answer for one capability, with the exact caption to render next to the disabled control.</summary>
public sealed record PermissionResult
{
    public required Capability Capability { get; init; }
    public required PermissionState State { get; init; }

    /// <summary>Scopes still missing; empty unless <see cref="State"/> is <see cref="PermissionState.NeedsConsent"/>.</summary>
    public IReadOnlyList<string> MissingScopes { get; init; } = [];

    /// <summary>UX §6 caption, or null when nothing needs explaining.</summary>
    public string? Reason { get; init; }

    /// <summary>False only when consent is missing; a role warning still lets the admin try (Graph returns the real answer).</summary>
    public bool CanRun => State != PermissionState.NeedsConsent;

    public bool NeedsConsent => State == PermissionState.NeedsConsent;
}
