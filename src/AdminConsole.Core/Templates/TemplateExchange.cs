namespace AdminConsole.Core.Templates;

/// <summary>
/// The mailbox half of a template (M17.1): applied once the mailbox exists, which for a hybrid user is after Entra Connect
/// has brought the account up and a licence has been assigned.
/// </summary>
public sealed record TemplateExchange
{
    /// <summary><c>en-GB</c>; null leaves Exchange's default.</summary>
    public string? Language { get; init; }

    /// <summary>A Windows time zone id (<c>GMT Standard Time</c>); null leaves Exchange's default.</summary>
    public string? TimeZone { get; init; }

    /// <summary>Shared mailboxes the role works in.</summary>
    public IReadOnlyList<TemplateMailboxAccess> MailboxAccess { get; init; } = [];
}

/// <summary>Access to one shared mailbox that comes with the role.</summary>
public sealed record TemplateMailboxAccess
{
    /// <summary>The mailbox owner's Entra object id.</summary>
    public required string MailboxId { get; init; }

    public string? Label { get; init; }
    public bool FullAccess { get; init; } = true;
    public bool SendAs { get; init; }
}
