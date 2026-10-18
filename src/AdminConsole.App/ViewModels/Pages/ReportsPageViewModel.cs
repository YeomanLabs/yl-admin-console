using AdminConsole.App.Services;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// M19 — reports: MFA readiness, licence usage, mailbox size and delegation. Each is built on request, saved per tenant
/// with its date, and shown from the saved copy when the page opens.
/// </summary>
public sealed class ReportsPageViewModel : IPageViewModel
{
    public ReportsPageViewModel(MfaReportSection mfa, LicenceReportSection licences, MailboxReportSection mailboxes, FilePicker files)
    {
        Mfa = mfa;
        Licences = licences;
        Mailboxes = mailboxes;
        mfa.Files = files;
        licences.Files = files;
        mailboxes.Files = files;
    }

    public string Title => "Reports";

    public MfaReportSection Mfa { get; }
    public LicenceReportSection Licences { get; }
    public MailboxReportSection Mailboxes { get; }

    /// <summary>Set by the shell: the status bar.</summary>
    public Action<string>? ReportStatus
    {
        set
        {
            Mfa.ReportStatus = value;
            Licences.ReportStatus = value;
            Mailboxes.ReportStatus = value;
        }
    }

    /// <summary>Set by the shell: opens a person's page from any report row that is a person.</summary>
    public Action<string, string>? OpenUser
    {
        set
        {
            Mfa.OpenUser = value;
            Licences.OpenUser = value;
            Mailboxes.OpenUser = value;
        }
    }

    /// <summary>Called once the page is on screen: shows each saved report without rebuilding anything.</summary>
    public async Task StartAsync()
    {
        await Mfa.LoadSavedAsync();
        await Licences.LoadSavedAsync();
        await Mailboxes.LoadSavedAsync();
    }
}
