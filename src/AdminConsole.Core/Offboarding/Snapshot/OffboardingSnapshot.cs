namespace AdminConsole.Core.Offboarding.Snapshot;

/// <summary>
/// Everything an account had the moment before an offboarding changed it (M15.1): the record that makes a rehire, or
/// a run against the wrong person, recoverable. Written to disk before the first change, and read back by Restore.
/// </summary>
/// <remarks>
/// It records what can be put back and what an admin would want to know was there, and nothing secret: no password,
/// no authentication method details, no mailbox content. Each section that could not be read is named in
/// <see cref="Gaps"/> rather than silently left empty, because "no groups" and "groups unknown" restore very differently.
/// </remarks>
public sealed record OffboardingSnapshot
{
    public int Version { get; init; } = 1;

    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required string UserPrincipalName { get; init; }
    public string? DisplayName { get; init; }
    public required DateTimeOffset TakenAt { get; init; }
    public string? TakenBy { get; init; }

    /// <summary>The offboarding run it belongs to, so the Activity page and the snapshot point at each other.</summary>
    public string? CorrelationId { get; init; }

    // ---- Entra -------------------------------------------------------------------------------------------------

    public bool AccountEnabled { get; init; }
    public bool Synced { get; init; }
    public string? UsageLocation { get; init; }
    public SnapshotRef? Manager { get; init; }

    /// <summary>Cloud groups, distribution lists included, each with whether a restore can put the user back in it.</summary>
    public IReadOnlyList<SnapshotGroup> Groups { get; init; } = [];

    /// <summary>Directly assigned licences only: group-assigned ones come back with the group.</summary>
    public IReadOnlyList<SnapshotRef> Licences { get; init; } = [];

    /// <summary>The attributes offboarding may stamp over, as they were.</summary>
    public IReadOnlyDictionary<string, string?> Attributes { get; init; } = new Dictionary<string, string?>();

    public int AuthMethodCount { get; init; }

    public IReadOnlyList<string> Devices { get; init; } = [];

    // ---- on-prem AD ----------------------------------------------------------------------------------------------

    public SnapshotOnPrem? OnPrem { get; init; }

    // ---- Exchange --------------------------------------------------------------------------------------------------

    public SnapshotMailbox? Mailbox { get; init; }

    /// <summary>What could not be read, in words — "Exchange: the permission isn't granted", "AD: no controller reachable".</summary>
    public IReadOnlyList<string> Gaps { get; init; } = [];
}

/// <summary>Something referred to by id with the name it had, so the snapshot reads well after the object is gone.</summary>
public sealed record SnapshotRef(string Id, string? Name);

public sealed record SnapshotGroup(string Id, string? Name, string Kind, bool Synced, bool Dynamic, bool MailEnabledList);

public sealed record SnapshotOnPrem
{
    public required Guid ObjectGuid { get; init; }
    public required string SamAccountName { get; init; }
    public required string DistinguishedName { get; init; }

    /// <summary>The OU it lived in — where Restore moves it back to.</summary>
    public required string ParentDn { get; init; }

    public bool Enabled { get; init; }
    public IReadOnlyList<string> GroupDns { get; init; } = [];
    public string? ManagerDn { get; init; }
}

public sealed record SnapshotMailbox
{
    public required string RecipientType { get; init; }
    public bool HiddenFromAddressLists { get; init; }
    public string? ForwardingAddress { get; init; }
    public string? ForwardingSmtpAddress { get; init; }
    public bool DeliverToMailboxAndForward { get; init; }
    public bool LitigationHold { get; init; }
    public string? AutoReplyState { get; init; }
    public IReadOnlyList<string> SendOnBehalf { get; init; } = [];
    public IReadOnlyList<string> FullAccess { get; init; } = [];
    public IReadOnlyList<string> SendAs { get; init; } = [];
    public IReadOnlyList<string> EnabledInboxRules { get; init; } = [];
}
