namespace AdminConsole.Core.OnPrem;

/// <summary>
/// What one on-prem-mastered field on the User 360 identity sheet should do (M12.4).
/// </summary>
/// <param name="LdapName">The attribute it writes to, or null when it stays read-only.</param>
/// <param name="Caption">
/// Shown beside the field either way: where an editable one writes to, or why a locked one is locked. Never null —
/// a greyed control with no reason is the thing CLAUDE.md's second non-negotiable forbids.
/// </param>
public sealed record HybridFieldState(string? LdapName, string Caption)
{
    public bool CanEdit => LdapName is not null;
}
