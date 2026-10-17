namespace AdminConsole.Core.Cache;

/// <summary>The cached entity tables. Names match the <c>sync_state.entity</c> values in docs/05-DATA-MODEL.md.</summary>
public enum CacheEntity
{
    Users,
    Groups,
    Devices,
    ManagedDevices,
    Skus,
    Organization,

    /// <summary>Exchange Online recipient facts per user (M13.4). Refreshed on the device interval; no delta exists.</summary>
    Mailboxes,
}

public static class CacheEntityExtensions
{
    /// <summary>The <c>sync_state.entity</c> key.</summary>
    public static string Key(this CacheEntity entity) => entity switch
    {
        CacheEntity.Users => "users",
        CacheEntity.Groups => "groups",
        CacheEntity.Devices => "devices",
        CacheEntity.ManagedDevices => "managed_devices",
        CacheEntity.Skus => "skus",
        CacheEntity.Organization => "organization",
        CacheEntity.Mailboxes => "mailboxes",
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, null),
    };

    /// <summary>The backing table name (same as the key today; kept separate so the two can diverge).</summary>
    public static string Table(this CacheEntity entity) => entity.Key();

    /// <summary>The FTS5 table kept in sync by triggers, or null when the entity is not searchable.</summary>
    public static string? FtsTable(this CacheEntity entity) => entity switch
    {
        CacheEntity.Users => "users_fts",
        CacheEntity.Groups => "groups_fts",
        CacheEntity.ManagedDevices => "devices_fts",
        _ => null,
    };
}
