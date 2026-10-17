namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One label-and-value line in a read-only section: the AD object page and the Mailbox tab.</summary>
/// <param name="Caption">A second, quieter line under the value: what it means, or why it is the way it is.</param>
public sealed record FactRow(string Label, string Value, bool IsMono = false, string? Caption = null)
{
    /// <summary>What a screen reader announces for the row: an items control names each item by its ToString.</summary>
    public override string ToString() => Caption is null ? $"{Label}: {Value}" : $"{Label}: {Value}. {Caption}";
}
