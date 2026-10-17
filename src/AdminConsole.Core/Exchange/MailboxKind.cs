namespace AdminConsole.Core.Exchange;

/// <summary>What Exchange holds for a user, reduced to the distinctions an admin acts on.</summary>
public enum MailboxKind
{
    /// <summary>Not in the cache yet — Exchange has not been read since this user appeared, or not at all.</summary>
    Unknown,

    /// <summary>Known to Exchange, with no mailbox in this tenant.</summary>
    None,

    User,
    Shared,
    Room,
    Equipment,

    /// <summary>Mail-enabled, with the mailbox somewhere else: on-prem in a hybrid organisation, or a guest's own tenant.</summary>
    MailUser,

    /// <summary>System mailboxes (discovery, scheduling) and anything newer than this list.</summary>
    Other,
}
