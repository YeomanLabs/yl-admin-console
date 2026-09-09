namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>
/// One thing the admin can try to do, with the delegated scopes it needs and the directory roles that usually allow it
/// (doc 08: "scopes are necessary, not sufficient"). Drives the grey rule (UX §6) and the Permissions page (UX §12).
/// </summary>
public sealed record Capability
{
    public required string Id { get; init; }

    /// <summary>Plain-English name shown on the Permissions page, e.g. "Edit user properties".</summary>
    public required string Title { get; init; }

    /// <summary>Every scope the capability needs. All must be consented.</summary>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>Any one of these directory roles typically allows it. Empty means no role hint is known.</summary>
    public IReadOnlyList<string> RoleTemplateIds { get; init; } = [];

    /// <summary>Human role hint for the UX §6 caption, e.g. "User Administrator or Helpdesk Administrator".</summary>
    public string? RoleHint { get; init; }

    /// <summary>Sensitive tier (doc 08): consent is requested only when the admin clicks the reveal.</summary>
    public bool IsSensitive { get; init; }
}
