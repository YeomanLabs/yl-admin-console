using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Cache.Browse;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>M9.4 — every group, browsable, with what makes each one editable or not visible before it is opened.</summary>
public sealed class GroupsPageViewModel : ListPageViewModel<GroupRow>
{
    private readonly CacheDb _cache;

    public GroupsPageViewModel(CacheDb cache, ILogger<GroupsPageViewModel> logger)
        : base(GroupBrowse.Filters, GroupBrowse.Sorts, GroupBrowse.DefaultSort, logger)
        => _cache = cache;

    public override string Title => "Groups";

    public override string Subtitle => "Security, Microsoft 365 and distribution groups. Click a group to see its members.";

    public override string SearchPlaceholder => "Filter by name, address or description";

    protected override string EmptyWhenNothingCached => "No groups cached yet";

    protected override string EmptyHintWhenNothingCached =>
        "The first sync may still be running — the status bar at the bottom says where it is up to.";

    protected override BrowsePage<GroupRow> Load(BrowseQuery query)
    {
        var page = _cache.BrowseGroups(query);
        return new BrowsePage<GroupRow>([.. page.Rows.Select(r => new GroupRow(r))], page.Total, page.Skip);
    }

    protected override IReadOnlyDictionary<string, int> CountChips(BrowseQuery query) => _cache.CountGroupFilters(query);
}
