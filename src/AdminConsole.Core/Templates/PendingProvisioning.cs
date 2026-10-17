namespace AdminConsole.Core.Templates;

/// <summary>Where a hybrid create has got to (M17.3).</summary>
public enum PendingState
{
    /// <summary>Made in AD; Entra Connect has not brought it up yet.</summary>
    WaitingForSync,

    /// <summary>The cloud half is done; the mailbox half waits for the mailbox to exist.</summary>
    WaitingForMailbox,

    Done,

    /// <summary>The admin gave up on it. Nothing more happens to it.</summary>
    Abandoned,
}

/// <summary>
/// A person created in AD whose cloud and mailbox halves are still to come (M17.3). Persisted, so a restart in the 30
/// minutes before Entra Connect runs does not lose the second half of an onboarding.
/// </summary>
public sealed record PendingProvisioning
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public required string DisplayName { get; init; }
    public required string AdUserPrincipalName { get; init; }
    public required string SamAccountName { get; init; }
    public required Guid AdGuid { get; init; }
    public string? TemplateName { get; init; }

    /// <summary>The source anchor Entra Connect gives the cloud account: base64 of the AD GUID.</summary>
    public string ImmutableId => Convert.ToBase64String(AdGuid.ToByteArray());

    public string? UsageLocation { get; init; }
    public IReadOnlyList<string> SkuIds { get; init; } = [];
    public IReadOnlyList<TemplateGroup> Groups { get; init; } = [];
    public TemplateExchange? Exchange { get; init; }

    public PendingState State { get; init; } = PendingState.WaitingForSync;
    public string? CloudUserId { get; init; }
    public string? CloudUserPrincipalName { get; init; }
    public DateTimeOffset? SyncedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>True when a licence went on, so a mailbox is worth waiting for.</summary>
    public bool Licensed { get; init; }

    public IReadOnlyList<PendingNote> Notes { get; init; } = [];

    public bool IsOpen => State is PendingState.WaitingForSync or PendingState.WaitingForMailbox;
    public bool HasProblems => Notes.Any(n => n.Problem);

    /// <summary>How long Entra Connect took to bring it up, the number the M17 gate measures.</summary>
    public TimeSpan? WaitedForSync => SyncedAt - CreatedAt;

    public string StateText => State switch
    {
        PendingState.WaitingForSync => "Waiting for Entra Connect to bring the account up",
        PendingState.WaitingForMailbox => "Waiting for the mailbox",
        PendingState.Abandoned => "Abandoned",
        _ => HasProblems ? "Finished, with problems" : "Finished",
    };

    public PendingProvisioning With(string note, bool problem = false) =>
        this with { Notes = [.. Notes, new PendingNote(DateTimeOffset.UtcNow, note, problem)] };
}

/// <summary>One line of a pending item's history.</summary>
public sealed record PendingNote(DateTimeOffset At, string Text, bool Problem = false);
