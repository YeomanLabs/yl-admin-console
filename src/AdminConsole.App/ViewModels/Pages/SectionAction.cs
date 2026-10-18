using CommunityToolkit.Mvvm.Input;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>A button under a Mailbox tab section — "Add address…", "Add person…".</summary>
public sealed record SectionAction(string Label, IAsyncRelayCommand Command)
{
    public override string ToString() => Label;

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so a refilled list whose
    // rows equal the ones before reused their detached peers, and a screen reader lost the rows (0.3.3).
    public bool Equals(SectionAction? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
