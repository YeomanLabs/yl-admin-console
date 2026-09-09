namespace YeomanLabs.AdminConsole.Core.Sync;

/// <summary>Bound from the <c>Sync</c> section of appsettings.json (doc 03 §Cache &amp; sync).</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>How often the users/groups/devices delta links are polled.</summary>
    public int DeltaIntervalSeconds { get; set; } = 120;

    /// <summary>How often Intune managed devices, SKUs and the organization are fully re-read (no delta exists).</summary>
    public int DeviceRefreshIntervalSeconds { get; set; } = 600;

    /// <summary>Rows per write transaction during walks.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary><c>$top</c> for directory delta pages (max 999) and managed devices (max 1000).</summary>
    public int DirectoryPageSize { get; set; } = 999;

    public int ManagedDevicePageSize { get; set; } = 1000;

    public TimeSpan DeltaInterval => TimeSpan.FromSeconds(Math.Max(10, DeltaIntervalSeconds));
    public TimeSpan DeviceRefreshInterval => TimeSpan.FromSeconds(Math.Max(30, DeviceRefreshIntervalSeconds));
}
