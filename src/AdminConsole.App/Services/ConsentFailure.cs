using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;

namespace AdminConsole.App.Services;

/// <summary>
/// What a page says when asking for a permission did not work. A cancelled prompt is a toast — nothing happened. Anything
/// else is a notice with the whole message, because it usually says who has to approve the permission and gives the link
/// to send them (0.3.5); the pages used to show "Write permission was not granted." and nothing else.
/// </summary>
public static class ConsentFailure
{
    public static async Task ShowAsync(IDialogService dialogs, ToastService toasts, SignInException ex)
    {
        if (ex.Kind == SignInFailureKind.Cancelled)
        {
            toasts.Show("Consent was cancelled. Nothing changed.");
            return;
        }

        var title = ex.Kind == SignInFailureKind.AdminConsentRequired ? "An admin has to approve this permission" : "The permission was not granted";
        await dialogs.ShowAsync(new NoticeDialogViewModel(title, ex.Message));
    }

    /// <summary>The short form, for a field or a row that reports its own failure inline.</summary>
    public static string Inline(SignInException ex) => ex.Kind == SignInFailureKind.Cancelled ? "Consent was cancelled. Nothing changed." : ex.Message;
}
