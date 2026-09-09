using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YeomanLabs.AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// Shows a generated password once, after the reset has already succeeded. The value is never written to the cache,
/// the action log, or the app log, so closing this dialog is the last chance to read it.
/// </summary>
public sealed partial class PasswordRevealDialogViewModel : DialogViewModelBase
{
    public PasswordRevealDialogViewModel(string userLabel, string password, bool forceChange)
    {
        UserLabel = userLabel;
        Password = password;
        ForceChangeText = forceChange
            ? "They will be asked to choose a new password at their next sign-in."
            : "They will not be asked to change it at their next sign-in.";
    }

    public string UserLabel { get; }

    public string Password { get; }

    public string ForceChangeText { get; }

    public override string Title => "Password reset";

    public override string ConfirmText => "Done";

    public override bool ShowCancel => false;

    [ObservableProperty]
    private bool _isCopied;

    [RelayCommand]
    private void Copy()
    {
        // Clipboard.SetText intermittently throws when another process holds the clipboard; a failed copy must not
        // take the dialog down and lose the only copy of the password.
        try
        {
            System.Windows.Clipboard.SetDataObject(Password, true);
            IsCopied = true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            IsCopied = false;
        }
    }
}
