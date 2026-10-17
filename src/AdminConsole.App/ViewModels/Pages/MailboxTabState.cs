namespace AdminConsole.App.ViewModels.Pages;

/// <summary>Where the Mailbox tab is. Every state but Loaded shows a sentence saying why there is nothing else.</summary>
public enum MailboxTabState
{
    NotLoaded,
    Loading,
    NeedsConsent,
    NoMailbox,
    Failed,
    Loaded,
}
