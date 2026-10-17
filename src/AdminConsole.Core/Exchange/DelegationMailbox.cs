namespace AdminConsole.Core.Exchange;

/// <summary>One mailbox as <c>Get-Mailbox -ResultSize Unlimited</c> lists it for the delegation report.</summary>
public sealed record DelegationMailbox(string UserId, string Identity, string Name, string? PrimarySmtp, MailboxKind Kind, IReadOnlyList<string> SendOnBehalf);
