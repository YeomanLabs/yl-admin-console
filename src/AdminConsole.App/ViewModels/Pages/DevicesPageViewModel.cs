using Microsoft.Extensions.Logging;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Cache.Browse;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// M9.3 — every device from both directories. Entra and Intune are unioned rather than tabbed, because "is this
/// laptop enrolled" is the question, and two tabs make the admin answer it themselves.
/// </summary>
public sealed class DevicesPageViewModel : ListPageViewModel<DeviceRow>
{
    private readonly CacheDb _cache;

    public DevicesPageViewModel(CacheDb cache, ILogger<DevicesPageViewModel> logger)
        : base(DeviceBrowse.Filters, DeviceBrowse.Sorts, DeviceBrowse.DefaultSort, logger)
        => _cache = cache;

    public override string Title => "Devices";

    public override string Subtitle => "Everything registered in Entra or enrolled in Intune. Click a device to open it.";

    public override string SearchPlaceholder => "Filter by device name, serial, model or user — or os=windows compliance=noncompliant encrypted=false";

    protected override string EmptyWhenNothingCached => "No devices cached yet";

    protected override string EmptyHintWhenNothingCached =>
        "Intune devices refresh on an interval rather than continuously — Ctrl+Shift+S syncs now.";

    protected override BrowsePage<DeviceRow> Load(BrowseQuery query)
    {
        var page = _cache.BrowseDevices(query);
        return new BrowsePage<DeviceRow>([.. page.Rows.Select(r => new DeviceRow(r))], page.Total, page.Skip) { Problems = page.Problems };
    }

    protected override IReadOnlyList<ListColumn<DeviceRow>> ColumnCatalog { get; } =
    [
        new("name", "Device", r => r.Primary),
        new("user", "Primary user", r => r.Model.PrimaryUserUpn ?? r.Model.PrimaryUserName),
        new("os", "OS", r => r.OsText),
        new("compliance", "Compliance", r => r.Model.Compliance),
        new("lastseen", "Last seen", r => r.Model.LastActivity?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)),
        new("serial", "Serial", r => r.Model.Serial),
        new("model", "Model", r => r.Model.Model, DefaultOn: false),
        new("encrypted", "Encrypted", r => r.Model.Encrypted is { } e ? (e ? "Yes" : "No") : null, DefaultOn: false),
        new("source", "Managed by", r => r.Model.Source.ToString(), DefaultOn: false),
        new("id", "Object ID", r => r.Id, DefaultOn: false),
    ];

    protected override IReadOnlyDictionary<string, int> CountChips(BrowseQuery query) => _cache.CountDeviceFilters(query);
}
