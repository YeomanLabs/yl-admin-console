using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// Asks for one line of text — a template name, a profile name. Confirm stays disabled until something is typed, so
/// the caller never has to handle an empty answer that looked like a yes.
/// </summary>
public sealed partial class TextPromptDialogViewModel : DialogViewModelBase
{
    public TextPromptDialogViewModel(string heading, string prompt, string? placeholder = null)
    {
        Heading = heading;
        Prompt = prompt;
        Placeholder = placeholder ?? string.Empty;
    }

    public string Heading { get; }

    /// <summary>The label above the box; the inherited <c>Message</c> carries any longer explanation.</summary>
    public string Prompt { get; }

    public string Placeholder { get; }

    public override string Title => Heading;

    public override string ConfirmText => "OK";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _value = string.Empty;

    /// <summary>For an optional answer, where empty means "use the default" rather than nothing typed yet.</summary>
    public bool AllowEmpty { get; init; }

    /// <summary>A check on what was typed; the message shows under the box and holds Confirm back.</summary>
    public Func<string, string?>? Validate { get; init; }

    public string? Problem => Value.Trim().Length == 0 ? null : Validate?.Invoke(Value.Trim());

    public override bool CanConfirm => (AllowEmpty || Value.Trim().Length > 0) && Problem is null;

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(Problem));
}
