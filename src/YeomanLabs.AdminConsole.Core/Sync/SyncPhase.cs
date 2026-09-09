namespace YeomanLabs.AdminConsole.Core.Sync;

public enum SyncPhase
{
    /// <summary>First full walk of a delta entity (no delta link yet, or after a 410 rebuild).</summary>
    Initial,

    /// <summary>Incremental tick against a stored delta link.</summary>
    Delta,

    /// <summary>Full re-read of an entity that has no delta (managed devices, SKUs, organization).</summary>
    Refresh,
}
