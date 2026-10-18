namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One row of the Identity property sheet (UX §4 tab 1). Read-only in M4; M5.2 adds edit-on-blur.</summary>
public sealed record IdentityField(string Label, string PropertyName, string? Value, bool IsMono = false, bool IsReadOnly = false, string? ReadOnlyReason = null)
{
    public string Display => string.IsNullOrEmpty(Value) ? "—" : Value;
    public bool HasValue => !string.IsNullOrEmpty(Value);
    public bool HasReadOnlyReason => !string.IsNullOrEmpty(ReadOnlyReason);

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so a refilled list whose
    // rows equal the ones before reused their detached peers, and a screen reader lost the rows (0.3.3).
    public bool Equals(IdentityField? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
