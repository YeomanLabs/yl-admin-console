namespace AdminConsole.Core.Cache;

/// <summary>One row of <c>sync_state</c>: where the delta walk for an entity left off.</summary>
public sealed record SyncState
{
    public required CacheEntity Entity { get; init; }
    public string? DeltaLink { get; init; }
    public DateTimeOffset? LastFullSync { get; init; }
    public DateTimeOffset? LastDeltaSync { get; init; }
    public int RowCount { get; init; }

    /// <summary>The most recent of full/delta sync, for the "last synced n min ago" caption.</summary>
    public DateTimeOffset? LastSync =>
        LastDeltaSync is { } d && (LastFullSync is not { } f || d > f) ? d : LastFullSync;
}
