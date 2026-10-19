using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Models;
using AdminConsole.Core.Users;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// Reset MFA from the person's header: the preview of what a reset removes — every method Graph lets go, in the order
/// Entra accepts — what stays and why, and the two things a helpdesk usually does with it: sign them out everywhere, so
/// the next sign-in asks them to register again, and hand them a Temporary Access Pass to register with.
/// </summary>
public sealed partial class ResetMfaDialogViewModel : DialogViewModelBase
{
    private readonly string _userLabel;

    public ResetMfaDialogViewModel(string userLabel, IReadOnlyList<AuthMethod> methods)
    {
        _userLabel = userLabel;
        Rows = AuthMethodReset.RemovalOrder(methods).Select(m => new ResetMfaMethodRow(m, Changed)).ToList();
        Kept = AuthMethodReset.Kept(methods).Select(k => $"{k.Method.KindLabel}: {k.Reason}").ToList();
    }

    public override string Title => $"Reset MFA for {_userLabel}";

    public override double Width => 600;

    public override bool IsDestructive => true;

    public override string ConfirmText => Selected.Count == 1 ? "Remove 1 method" : $"Remove {Selected.Count} methods";

    public override bool CanConfirm => Selected.Count > 0;

    public IReadOnlyList<ResetMfaMethodRow> Rows { get; }

    public bool HasRows => Rows.Count > 0;

    public IReadOnlyList<string> Kept { get; }

    public bool HasKept => Kept.Count > 0;

    public string Intro => Rows.Count == 0
        ? $"{_userLabel} has nothing registered that can be removed."
        : $"These are removed, in this order, and nothing here can put them back: only {_userLabel} can register a method again. At their next sign-in Entra asks them to register — and so it would anyone else who has their password.";

    /// <summary>Signing them out makes the next sign-in the one that asks them to register again.</summary>
    [ObservableProperty]
    private bool _revokeSessions = true;

    /// <summary>A pass to register the new methods with, shown once at the end.</summary>
    [ObservableProperty]
    private bool _issueTapAfter;

    public IReadOnlyList<AuthMethod> Selected => Rows.Where(r => r.IsChecked).Select(r => r.Method).ToList();

    private void Changed()
    {
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CanConfirm));
    }
}
