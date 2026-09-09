using YeomanLabs.AdminConsole.Core.Cache;

namespace YeomanLabs.AdminConsole.Core.Sync;

/// <summary>What the shell needs from the sync loop. Events fire on thread-pool threads; the shell VM marshals.</summary>
public interface ISyncStatus
{
    event EventHandler<SyncProgress>? ProgressChanged;

    /// <summary>True once users, groups and Entra devices all have a delta link (the palette can drop "results may be incomplete").</summary>
    bool IsInitialSyncComplete { get; }

    bool IsRunning { get; }

    /// <summary>The most recent progress event per entity.</summary>
    IReadOnlyDictionary<CacheEntity, SyncProgress> Latest { get; }

    DateTimeOffset? LastCycleCompleted { get; }
    Exception? LastError { get; }

    /// <summary>Wakes the loop now. Returns when the resulting cycle has finished.</summary>
    Task SyncNowAsync(CancellationToken ct);
}
