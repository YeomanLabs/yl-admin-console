using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// The password of the account an on-prem domain connects as, once per run of the console. Said plainly where it goes:
/// memory, until the console closes — never a file, never the log.
/// </summary>
public sealed partial class AdSignInDialogViewModel : DialogViewModelBase
{
    public AdSignInDialogViewModel(string domain, string account)
    {
        Domain = domain;
        Account = account;
    }

    public string Domain { get; }

    public string Account { get; }

    public override string Title => $"Sign in to {Domain}";

    public override string ConfirmText => "Sign in";

    public override bool CanConfirm => Password.Length > 0;

    public string Explanation =>
        $"On-prem features for this tenant connect to {Domain} as {Account}. The password is kept in memory until the console closes — never saved or logged.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _password = string.Empty;
}
