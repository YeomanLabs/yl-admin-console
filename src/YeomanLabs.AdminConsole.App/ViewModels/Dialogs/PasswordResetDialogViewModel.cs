using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// Reset password (UX §4 header). Either generate one — shown once after the reset succeeds — or type one. The
/// force-change checkbox maps to <c>forceChangePasswordNextSignIn</c>. Nothing here is logged.
/// </summary>
public sealed partial class PasswordResetDialogViewModel : DialogViewModelBase
{
    /// <summary>Graph's own floor is 8; the tenant policy may demand more and will answer 400 if it does.</summary>
    public const int MinimumLength = 8;

    public PasswordResetDialogViewModel(string userLabel)
    {
        UserLabel = userLabel;
        _generated = UserService.GeneratePassword();
        Message = $"The current password for {userLabel} stops working straight away. Sessions that are already signed in keep working until you revoke them.";
    }

    public string UserLabel { get; }

    public override string Title => "Reset password";

    public override string ConfirmText => "Reset password";

    public override bool IsDestructive => true;

    /// <summary>True: a random password the admin never has to invent. False: they type their own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTyped))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(TypedError))]
    private bool _isGenerated = true;

    /// <summary>
    /// The other half of the radio pair. RadioButton.IsChecked binds two-way, so this has to be settable — a read-only
    /// property here throws while the template loads and takes the whole dialog with it.
    /// </summary>
    public bool IsTyped
    {
        get => !IsGenerated;
        set => IsGenerated = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _generated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(TypedError))]
    private string _typed = string.Empty;

    [ObservableProperty]
    private bool _forceChangeNextSignIn = true;

    /// <summary>The value that will be sent to Graph.</summary>
    public string Password => IsGenerated ? Generated : Typed;

    public string? TypedError => IsGenerated || Typed.Length == 0 || Typed.Length >= MinimumLength
        ? null
        : $"At least {MinimumLength} characters. Your tenant's policy may require more.";

    public override bool CanConfirm => Password.Length >= MinimumLength;

    [RelayCommand]
    private void Regenerate() => Generated = UserService.GeneratePassword();
}
