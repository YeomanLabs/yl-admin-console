namespace AdminConsole.Core.Exchange;

/// <summary>Every mailbox, with what the two tenant-wide calls can say about delegation (M19.3).</summary>
public sealed record DelegationOverview(IReadOnlyList<DelegationMailbox> Mailboxes, IReadOnlyDictionary<string, IReadOnlyList<string>> SendAs);
