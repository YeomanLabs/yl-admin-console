using AdminConsole.Core.Cache;

namespace AdminConsole.Core.Sync;

/// <summary>One progress event from the sync loop (doc 03: entity, processed, done?). Raised on a thread-pool thread.</summary>
public sealed record SyncProgress
{
    public required CacheEntity Entity { get; init; }
    public required SyncPhase Phase { get; init; }

    /// <summary>Objects applied so far in this phase (adds, updates and removals).</summary>
    public required int Processed { get; init; }

    /// <summary>Expected total for the initial walk (from <c>$count</c>), when known.</summary>
    public int? Total { get; init; }

    public required bool IsDone { get; init; }

    /// <summary>Set when the phase ended in failure; <see cref="IsDone"/> is also true.</summary>
    public Exception? Error { get; init; }

    public double? Fraction => Total is > 0 ? Math.Min(1.0, (double)Processed / Total.Value) : null;
}
