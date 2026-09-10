using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// The standard confirm (UX §2: destructive actions show a preview and name what will happen). Set
/// <see cref="RequiredTypedValue"/> to demand the admin types the object's name first, as the wipe flow does.
/// </summary>
public sealed partial class ConfirmDialogViewModel : DialogViewModelBase
{
    public override string Title => Heading;

    public required string Heading { get; init; }

    public override string ConfirmText => ConfirmLabel;

    public string ConfirmLabel { get; init; } = "Confirm";

    public override bool IsDestructive => Destructive;

    public bool Destructive { get; init; }

    /// <summary>When set, the confirm button stays disabled until <see cref="TypedValue"/> matches it exactly.</summary>
    public string? RequiredTypedValue { get; init; }

    public bool NeedsTyping => RequiredTypedValue is not null;

    public string TypedPrompt => RequiredTypedValue is null ? string.Empty : $"Type {RequiredTypedValue} to confirm";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _typedValue = string.Empty;

    public override bool CanConfirm =>
        RequiredTypedValue is null || string.Equals(TypedValue.Trim(), RequiredTypedValue, StringComparison.OrdinalIgnoreCase);
}
