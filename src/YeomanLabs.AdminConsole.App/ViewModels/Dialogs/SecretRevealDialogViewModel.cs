using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YeomanLabs.AdminConsole.App.Services;

namespace YeomanLabs.AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// Shows revealed secrets (UX §5 Security): BitLocker recovery keys and the LAPS password. The values live in this
/// object and the clipboard only — never in the cache, the action log, or a toast. The reveal itself is already logged.
/// </summary>
public sealed class SecretRevealDialogViewModel : DialogViewModelBase
{
    public SecretRevealDialogViewModel(string heading, IReadOnlyList<SecretRow> secrets)
    {
        Heading = heading;
        Secrets = secrets;
    }

    public string Heading { get; }

    public override string Title => Heading;

    public override string ConfirmText => "Close";

    public override bool ShowCancel => false;

    public IReadOnlyList<SecretRow> Secrets { get; }
}

/// <summary>One revealed value with its label and a copy button.</summary>
public sealed partial class SecretRow : ObservableObject
{
    public SecretRow(string label, string value, string? caption = null)
    {
        Label = label;
        Value = value;
        Caption = caption;
    }

    public string Label { get; }
    public string Value { get; }

    /// <summary>Context under the value: when it was backed up, which account it belongs to.</summary>
    public string? Caption { get; }

    public bool HasCaption => Caption is not null;

    [ObservableProperty]
    private bool _isCopied;

    [RelayCommand]
    private void Copy() => IsCopied = ClipboardText.TryCopy(Value);
}
