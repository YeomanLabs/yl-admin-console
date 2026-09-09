using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Cache.Browse;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

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

    public override string SearchPlaceholder => "Filter by device name, serial, model or user";

    protected override string EmptyWhenNothingCached => "No devices cached yet";

    protected override string EmptyHintWhenNothingCached =>
        "Intune devices refresh on an interval rather than continuously — Ctrl+Shift+S syncs now.";

    protected override BrowsePage<DeviceRow> Load(BrowseQuery query)
    {
        var page = _cache.BrowseDevices(query);
        return new BrowsePage<DeviceRow>([.. page.Rows.Select(r => new DeviceRow(r))], page.Total, page.Skip);
    }

    protected override IReadOnlyDictionary<string, int> CountChips(BrowseQuery query) => _cache.CountDeviceFilters(query);
}
