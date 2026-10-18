namespace AdminConsole.App.Services;

/// <summary>One requested permission explained in plain English (UX §9 step 2, and the grey rule's "Needs the '{Scope}' permission").</summary>
public sealed record ScopeExplanation(string Scope, string Title, string Why)
{
    // Identity, not value equality: WPF keys a list's automation peers by item equality, so a refilled list whose
    // rows equal the ones before reused their detached peers, and a screen reader lost the rows (0.3.3).
    public bool Equals(ScopeExplanation? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
