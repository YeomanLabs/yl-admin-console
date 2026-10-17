namespace AdminConsole.Core.Exchange;

/// <summary>
/// The cached mailbox facts for one user (doc 13 §2): enough to badge a row, filter a list and decide whether the Mailbox
/// tab has anything to load. Everything else is read live when the tab opens.
/// </summary>
public sealed record MailboxSummary
{
    /// <summary>Entra object id — Exchange's <c>ExternalDirectoryObjectId</c>.</summary>
    public required string UserId { get; init; }

    /// <summary><c>RecipientTypeDetails</c> exactly as Exchange reports it, kept so a value newer than <see cref="MailboxKind"/> is not lost.</summary>
    public required string RecipientType { get; init; }

    public string? PrimarySmtp { get; init; }
    public bool HiddenFromAddressLists { get; init; }
    public bool DirSynced { get; init; }

    public MailboxKind Kind => MailboxKinds.From(RecipientType);

    public bool HasMailbox => Kind is MailboxKind.User or MailboxKind.Shared or MailboxKind.Room or MailboxKind.Equipment;
}
