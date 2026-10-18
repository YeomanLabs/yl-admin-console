namespace AdminConsole.App.ViewModels.Pages;

/// <summary>A titled group of editable identity fields (Name, Job, Contact, Address, Settings).</summary>
public sealed record EditableSection(string Title, IReadOnlyList<EditableField> Fields)
{
    // Identity, not value equality: WPF keys a list's automation peers by item equality, so a refilled list whose
    // rows equal the ones before reused their detached peers, and a screen reader lost the rows (0.3.3).
    public bool Equals(EditableSection? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
