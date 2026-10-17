namespace AdminConsole.Core.Exchange;

/// <summary>The mailbox a write is aimed at: its owner's Entra object id, and the name the action log and toasts use.</summary>
public sealed record MailboxRef(string UserId, string Label);
