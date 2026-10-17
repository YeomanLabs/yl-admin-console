using System.Text.Json;

namespace AdminConsole.Core.Exchange;

/// <summary>The properties of one mailbox the Mailbox tab shows, from one <c>Get-Mailbox</c> (doc 13 §2).</summary>
public sealed record MailboxDetail
{
    /// <summary>What to ask <c>Get-Mailbox</c> for. Without a <c>$select</c> it answers with 338 properties.</summary>
    public static readonly IReadOnlyList<string> Select =
    [
        "ExternalDirectoryObjectId", "RecipientTypeDetails", "PrimarySmtpAddress", "Alias", "EmailAddresses",
        "HiddenFromAddressListsEnabled", "IsDirSynced",
        "ForwardingAddress", "ForwardingSmtpAddress", "DeliverToMailboxAndForward",
        "GrantSendOnBehalfTo", "MessageCopyForSentAsEnabled", "MessageCopyForSendOnBehalfEnabled",
        "ArchiveStatus", "ArchiveGuid", "ArchiveName", "AutoExpandingArchiveEnabled", "ArchiveQuota",
        "LitigationHoldEnabled", "LitigationHoldDuration", "LitigationHoldOwner", "LitigationHoldDate",
        "ProhibitSendReceiveQuota", "ProhibitSendQuota", "IssueWarningQuota",
        "WhenMailboxCreated",
        "AcceptMessagesOnlyFromSendersOrMembers", "RejectMessagesFromSendersOrMembers", "RequireSenderAuthenticationEnabled",
    ];

    public required string UserId { get; init; }
    public required string RecipientType { get; init; }
    public MailboxKind Kind => MailboxKinds.From(RecipientType);

    public string? PrimarySmtp { get; init; }
    public string? Alias { get; init; }
    public IReadOnlyList<ProxyAddress> EmailAddresses { get; init; } = [];
    public bool HiddenFromAddressLists { get; init; }
    public bool DirSynced { get; init; }

    /// <summary>An internal recipient, by Exchange identity.</summary>
    public string? ForwardingAddress { get; init; }

    /// <summary>An external address, as <c>smtp:someone@elsewhere.com</c>.</summary>
    public string? ForwardingSmtpAddress { get; init; }

    public bool DeliverToMailboxAndForward { get; init; }
    public bool IsForwarding => ForwardingAddress is not null || ForwardingSmtpAddress is not null;

    public IReadOnlyList<string> SendOnBehalf { get; init; } = [];
    public bool CopySentAsToSentItems { get; init; }
    public bool CopySendOnBehalfToSentItems { get; init; }

    public string? ArchiveStatus { get; init; }
    public bool HasArchive { get; init; }
    public string? ArchiveName { get; init; }
    public bool AutoExpandingArchive { get; init; }
    public ExchangeSize ArchiveQuota { get; init; }

    public bool LitigationHold { get; init; }

    /// <summary>Days, or <c>Unlimited</c>, as Exchange words it.</summary>
    public string? LitigationHoldDuration { get; init; }

    public string? LitigationHoldOwner { get; init; }
    public DateTimeOffset? LitigationHoldSince { get; init; }

    public ExchangeSize ProhibitSendReceiveQuota { get; init; }
    public ExchangeSize ProhibitSendQuota { get; init; }
    public ExchangeSize IssueWarningQuota { get; init; }

    public DateTimeOffset? Created { get; init; }

    public IReadOnlyList<string> AcceptOnlyFrom { get; init; } = [];
    public IReadOnlyList<string> RejectFrom { get; init; } = [];
    public bool RequireSenderAuthentication { get; init; }
    public bool HasDeliveryRestrictions => AcceptOnlyFrom.Count > 0 || RejectFrom.Count > 0 || RequireSenderAuthentication;

    public static MailboxDetail FromJson(string userId, JsonElement row)
    {
        var archiveGuid = ExchangeValues.NonEmpty(row, "ArchiveGuid");
        var archiveStatus = ExchangeValues.NonEmpty(row, "ArchiveStatus");
        return new MailboxDetail
        {
            UserId = ExchangeValues.NonEmpty(row, "ExternalDirectoryObjectId") ?? userId,
            RecipientType = ExchangeValues.NonEmpty(row, "RecipientTypeDetails") ?? MailboxKinds.UserMailbox,
            PrimarySmtp = ExchangeValues.NonEmpty(row, "PrimarySmtpAddress"),
            Alias = ExchangeValues.NonEmpty(row, "Alias"),
            EmailAddresses = [.. ExchangeValues.Strings(row, "EmailAddresses").Select(ProxyAddress.Parse)
                .OrderByDescending(a => a.IsPrimary).ThenByDescending(a => a.IsSmtp).ThenBy(a => a.Address, StringComparer.OrdinalIgnoreCase)],
            HiddenFromAddressLists = ExchangeValues.IsTrue(row, "HiddenFromAddressListsEnabled"),
            DirSynced = ExchangeValues.IsTrue(row, "IsDirSynced"),
            ForwardingAddress = ExchangeValues.NonEmpty(row, "ForwardingAddress"),
            ForwardingSmtpAddress = ExchangeValues.NonEmpty(row, "ForwardingSmtpAddress"),
            DeliverToMailboxAndForward = ExchangeValues.IsTrue(row, "DeliverToMailboxAndForward"),
            SendOnBehalf = ExchangeValues.Strings(row, "GrantSendOnBehalfTo"),
            CopySentAsToSentItems = ExchangeValues.IsTrue(row, "MessageCopyForSentAsEnabled"),
            CopySendOnBehalfToSentItems = ExchangeValues.IsTrue(row, "MessageCopyForSendOnBehalfEnabled"),
            ArchiveStatus = archiveStatus,
            // ArchiveStatus is "Active" once provisioned; a non-empty archive GUID covers the minutes in between.
            HasArchive = string.Equals(archiveStatus, "Active", StringComparison.OrdinalIgnoreCase) ||
                         (archiveGuid is not null && Guid.TryParse(archiveGuid, out var g) && g != Guid.Empty),
            ArchiveName = ExchangeValues.Strings(row, "ArchiveName").FirstOrDefault(),
            AutoExpandingArchive = ExchangeValues.IsTrue(row, "AutoExpandingArchiveEnabled"),
            ArchiveQuota = ExchangeValues.Size(row, "ArchiveQuota"),
            LitigationHold = ExchangeValues.IsTrue(row, "LitigationHoldEnabled"),
            LitigationHoldDuration = ExchangeValues.NonEmpty(row, "LitigationHoldDuration"),
            LitigationHoldOwner = ExchangeValues.NonEmpty(row, "LitigationHoldOwner"),
            LitigationHoldSince = ExchangeValues.Date(row, "LitigationHoldDate"),
            ProhibitSendReceiveQuota = ExchangeValues.Size(row, "ProhibitSendReceiveQuota"),
            ProhibitSendQuota = ExchangeValues.Size(row, "ProhibitSendQuota"),
            IssueWarningQuota = ExchangeValues.Size(row, "IssueWarningQuota"),
            Created = ExchangeValues.Date(row, "WhenMailboxCreated"),
            AcceptOnlyFrom = ExchangeValues.Strings(row, "AcceptMessagesOnlyFromSendersOrMembers"),
            RejectFrom = ExchangeValues.Strings(row, "RejectMessagesFromSendersOrMembers"),
            RequireSenderAuthentication = ExchangeValues.IsTrue(row, "RequireSenderAuthenticationEnabled"),
        };
    }
}
