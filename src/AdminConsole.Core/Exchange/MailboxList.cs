namespace AdminConsole.Core.Exchange;

/// <summary>The multi-valued recipient lists on a mailbox, each changed by add/remove so nobody unmentioned is dropped.</summary>
public enum MailboxList
{
    /// <summary><c>GrantSendOnBehalfTo</c>.</summary>
    SendOnBehalf,

    /// <summary><c>AcceptMessagesOnlyFromSendersOrMembers</c>: when non-empty, only these may send to the mailbox.</summary>
    AcceptOnlyFrom,

    /// <summary><c>RejectMessagesFromSendersOrMembers</c>.</summary>
    RejectFrom,
}
