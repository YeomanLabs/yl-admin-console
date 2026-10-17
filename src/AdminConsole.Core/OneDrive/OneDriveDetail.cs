using System.Globalization;
using System.Text.Json;
using AdminConsole.Core.Models;
using AdminConsole.Core.SharePoint;

namespace AdminConsole.Core.OneDrive;

/// <summary>
/// A user's OneDrive as the tab shows it (M16): where it is, how full, who may share from it, whether it is locked, and
/// who administers it. Read from the SharePoint admin API, which needs no access to the OneDrive itself.
/// </summary>
public sealed record OneDriveDetail
{
    public required string UserId { get; init; }

    /// <summary>The site URL, without a trailing slash (SharePoint rejects the slash in <c>GetSitePropertiesByUrl</c>).</summary>
    public required string Url { get; init; }

    public required SharePointHosts Hosts { get; init; }
    public required string SiteId { get; init; }
    public string? Owner { get; init; }
    public long StorageUsedMb { get; init; }
    public long StorageQuotaMb { get; init; }
    public OneDriveSharing Sharing { get; init; }
    public string LockState { get; init; } = OneDriveLock.Unlock;
    public DateTimeOffset? LastContentModified { get; init; }
    public IReadOnlyList<SiteAdmin> Admins { get; init; } = [];

    /// <summary>The tenant's own OneDrive sharing setting — no OneDrive can be more open than this.</summary>
    public OneDriveSharing TenantCeiling { get; init; } = OneDriveSharing.ExternalUserAndGuestSharing;

    public bool IsLocked => OneDriveLock.IsLocked(LockState);

    public double? FractionUsed => StorageQuotaMb > 0 ? (double)StorageUsedMb / StorageQuotaMb : null;

    public static OneDriveDetail FromJson(string userId, string url, SharePointHosts hosts, JsonElement props, IReadOnlyList<SiteAdmin> admins, OneDriveSharing ceiling) => new()
    {
        UserId = userId,
        Url = url,
        Hosts = hosts,
        SiteId = props.GetString("SiteId") ?? string.Empty,
        Owner = props.GetString("Owner"),
        StorageUsedMb = Long(props, "StorageUsage"),
        StorageQuotaMb = Long(props, "StorageMaximumLevel"),
        Sharing = (OneDriveSharing)(int)Long(props, "SharingCapability"),
        LockState = props.GetString("LockState") ?? OneDriveLock.Unlock,
        // SharePoint writes this without a zone; it is UTC.
        LastContentModified = DateTime.TryParse(props.GetString("LastContentModifiedDate"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) && d.Year > 1 ? new DateTimeOffset(d, TimeSpan.Zero) : null,
        Admins = admins,
        TenantCeiling = ceiling,
    };

    /// <summary>SharePoint sends some sizes as numbers and others as strings; read either.</summary>
    internal static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        } : 0;
}
