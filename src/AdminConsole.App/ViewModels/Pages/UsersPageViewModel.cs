using Microsoft.Extensions.Logging;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Cache.Browse;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>M9.2 — everyone in the tenant, browsable. The palette finds a person you can name; this is for the
/// questions that start "who are all the…".</summary>
public sealed class UsersPageViewModel : ListPageViewModel<UserRow>
{
    private readonly CacheDb _cache;

    public UsersPageViewModel(CacheDb cache, ILogger<UsersPageViewModel> logger)
        : base(HasMailboxData(cache) ? UserBrowse.Filters : UserBrowse.FiltersWithoutMailboxes, UserBrowse.Sorts, UserBrowse.DefaultSort, logger)
        => _cache = cache;

    /// <summary>Mailbox chips only once Exchange has been read: until then they would all count zero, which reads as a
    /// fact about the tenant rather than an absence of data.</summary>
    private static bool HasMailboxData(CacheDb cache) => cache.GetSyncState(CacheEntity.Mailboxes) is not null;

    public override string Title => "Users";

    public override string Subtitle => "Everyone in the directory. Click a person to open them.";

    public override string SearchPlaceholder => "Filter by name, sign-in name, job title or department";

    protected override string EmptyWhenNothingCached => "No users cached yet";

    protected override string EmptyHintWhenNothingCached =>
        "The first sync may still be running — the status bar at the bottom says where it is up to.";

    protected override BrowsePage<UserRow> Load(BrowseQuery query)
    {
        var page = _cache.BrowseUsers(query);
        return new BrowsePage<UserRow>([.. page.Rows.Select(r => new UserRow(r))], page.Total, page.Skip);
    }

    protected override IReadOnlyDictionary<string, int> CountChips(BrowseQuery query) => _cache.CountUserFilters(query);
}
