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

    public override bool CanConfirm => Value.Trim().Length > 0;
}
