using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>UX §9 step 1: splash with "Sign in with Microsoft", plus the three failure states.</summary>
public sealed partial class SignInPageViewModel : ObservableObject
{
    private readonly Func<Task> _onSignIn;

    public SignInPageViewModel(Func<Task> onSignIn) => _onSignIn = onSignIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "Signing in…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorTitle;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetails))]
    private string? _errorDetails;

    /// <summary>Shown for Conditional Access blocks (UX §9: "Contact your Entra admin").</summary>
    [ObservableProperty]
    private string? _errorHint;

    public bool HasError => ErrorTitle is not null;
    public bool HasErrorDetails => !string.IsNullOrEmpty(ErrorDetails);

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private Task SignIn() => _onSignIn();

    private bool CanSignIn() => !IsBusy;

    public void ClearError()
    {
        ErrorTitle = null;
        ErrorMessage = null;
        ErrorDetails = null;
        ErrorHint = null;
    }

    /// <summary>Maps the failure kinds to the UX §9 texts.</summary>
    public void ShowFailure(SignInException ex)
    {
        switch (ex.Kind)
        {
            case SignInFailureKind.Cancelled:
                ErrorTitle = "No account selected";
                ErrorMessage = "Sign-in was cancelled. Try again when you're ready.";
                break;
            case SignInFailureKind.ConsentDeclined:
                ErrorTitle = "Read access is required";
                ErrorMessage = "The app cannot function without read access to users, groups and devices. Retry and accept the permissions to continue.";
                break;
            case SignInFailureKind.ConditionalAccess:
                ErrorTitle = "Blocked by Conditional Access";
                ErrorMessage = ex.RawMessage ?? ex.Message;
                ErrorHint = "Contact your Entra admin.";
                break;
            default:
                ErrorTitle = "Sign-in failed";
                ErrorMessage = ex.Message;
                break;
        }

        ErrorDetails = ex.ErrorCode is null ? ex.RawMessage : $"{ex.ErrorCode}\n{ex.RawMessage}";
    }

    public void ShowFailure(Exception ex)
    {
        ErrorTitle = "Could not finish signing in";
        ErrorMessage = ex.Message;
        ErrorDetails = ex.ToString();
    }
}
