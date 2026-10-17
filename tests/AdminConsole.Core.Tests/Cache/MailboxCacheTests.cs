using FluentAssertions;
using Xunit;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Cache.Browse;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Models;
using AdminConsole.Core.Search;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Cache;

/// <summary>M13.4 — mailbox facts reach the Users list chips and the palette badges.</summary>
public sealed class MailboxCacheTests : IDisposable
{
    private readonly CacheDb _db = CacheDb.OpenInMemory();

    public MailboxCacheTests()
    {
        _db.Write(w =>
        {
            w.UpsertUser(User("u-person", "Ada Lovelace", "ada@contoso.com", enabled: true));
            w.UpsertUser(User("u-shared", "Contoso Info", "info@contoso.com", enabled: false));
            w.UpsertUser(User("u-room", "Boardroom", "boardroom@contoso.com", enabled: false));
            w.UpsertUser(User("u-nomailbox", "Joni Sherman", "joni@contoso.com", enabled: true));
            w.UpsertUser(User("u-unknown", "Brand New", "new@contoso.com", enabled: true));
            w.UpsertUser(User("u-leaver", "Cy Leaver", "cy@contoso.com", enabled: false));

            w.UpsertMailbox(Mailbox("u-person", "UserMailbox"));
            w.UpsertMailbox(Mailbox("u-shared", "SharedMailbox"));
            w.UpsertMailbox(Mailbox("u-room", "RoomMailbox"));
            w.UpsertMailbox(Mailbox("u-nomailbox", "User"));
            w.UpsertMailbox(Mailbox("u-leaver", "UserMailbox"));
            // u-unknown appeared after Exchange was last read: no row.
        });
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("mailbox-shared", new[] { "u-shared" })]
    [InlineData("mailbox-user", new[] { "u-leaver", "u-person" })]
    [InlineData("mailbox-resource", new[] { "u-room" })]
    [InlineData("mailbox-none", new[] { "u-nomailbox" })]
    public void MailboxChips_FindExactlyTheirKind(string key, string[] ids)
    {
        _db.BrowseUsers(new BrowseQuery { FilterKeys = [key] }).Rows.Select(r => r.Id).Should().BeEquivalentTo(ids);
    }

    [Fact]
    public void NoMailbox_NeverSweepsInAUserExchangeHasNotBeenAskedAbout()
    {
        _db.BrowseUsers(new BrowseQuery { FilterKeys = ["mailbox-none"] }).Rows.Should().NotContain(r => r.Id == "u-unknown");
    }

    [Fact]
    public void MailboxChips_OrWithinTheirGroup_AndAndWithOthers()
    {
        _db.BrowseUsers(new BrowseQuery { FilterKeys = ["mailbox-shared", "mailbox-resource"] }).Total.Should().Be(2);
        _db.BrowseUsers(new BrowseQuery { FilterKeys = ["mailbox-user", "disabled"] }).Rows.Single().Id.Should().Be("u-leaver");
    }

    [Fact]
    public void ChipCounts_IncludeTheMailboxGroup()
    {
        var counts = _db.CountUserFilters(new BrowseQuery());

        counts["mailbox-shared"].Should().Be(1);
        counts["mailbox-user"].Should().Be(2);
    }

    [Fact]
    public void WithoutMailboxData_TheCatalogOmitsTheMailboxGroup()
    {
        UserBrowse.FiltersWithoutMailboxes.Should().NotContain(f => f.Group == "mailbox");
        UserBrowse.FiltersWithoutMailboxes.Should().HaveCount(UserBrowse.Filters.Count - 4);
    }

    [Fact]
    public void Palette_BadgesASharedMailbox_AndDoesNotCallItDisabled()
    {
        var hit = Search("info").Single(h => h.Id == "u-shared");

        hit.Badges.Should().Contain("Shared mailbox").And.NotContain("Disabled",
            "a shared mailbox's account is disabled by design; calling it Disabled sends an admin looking for a leaver");
    }

    [Fact]
    public void Palette_StillCallsALeaverDisabled_AndBadgesNothingForAnOrdinaryMailbox()
    {
        Search("cy").Single(h => h.Id == "u-leaver").Badges.Should().Contain("Disabled").And.NotContain("Shared mailbox");
        Search("ada").Single(h => h.Id == "u-person").Badges.Should().BeEmpty();
        Search("brand").Single(h => h.Id == "u-unknown").Badges.Should().BeEmpty("unknown is not a fact to badge");
    }

    [Fact]
    public void ClearingTheCache_ClearsMailboxes_AndTheirSyncState()
    {
        _db.Write(w => w.SetSyncState(new SyncState { Entity = CacheEntity.Mailboxes, LastFullSync = DateTimeOffset.UtcNow, RowCount = 5 }));

        _db.Clear(CacheEntity.Mailboxes);

        _db.Count(CacheEntity.Mailboxes).Should().Be(0);
        _db.GetSyncState(CacheEntity.Mailboxes).Should().BeNull();
    }

    private IReadOnlyList<SearchHit> Search(string q) =>
        new SearchService(_db, [], new FakeTimeProvider()).Search(q, SearchScope.All, 8, CancellationToken.None).Users.ToList();

    private static DirectoryUser User(string id, string name, string upn, bool enabled) => new()
    {
        Id = id, UserPrincipalName = upn, DisplayName = name, Mail = upn, AccountEnabled = enabled,
        UserType = "Member", OnPremisesSyncEnabled = false, Licensed = enabled, Raw = "{}",
    };

    private static MailboxSummary Mailbox(string id, string type) => new() { UserId = id, RecipientType = type };
}
