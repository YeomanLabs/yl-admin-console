namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One label-and-value line in a read-only section: the AD object page and the Mailbox tab.</summary>
/// <param name="Caption">A second, quieter line under the value: what it means, or why it is the way it is.</param>
public sealed record FactRow(string Label, string Value, bool IsMono = false, string? Caption = null)
{
    /// <summary>What a screen reader announces for the row: an items control names each item by its ToString.</summary>
    public override string ToString() => Caption is null ? $"{Label}: {Value}" : $"{Label}: {Value}. {Caption}";

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so a refilled list whose
    // rows equal the ones before reused their detached peers, and a screen reader lost the rows (0.3.3).
    public bool Equals(FactRow? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
