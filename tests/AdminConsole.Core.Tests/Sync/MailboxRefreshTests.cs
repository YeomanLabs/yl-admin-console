using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Sync;
using AdminConsole.Core.Tests.Exchange;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Sync;

/// <summary>M13.4 — Exchange's recipient facts cached on the device interval, and never a sync error when Exchange is
/// simply not in use.</summary>
public sealed class MailboxRefreshTests : IDisposable
{
    private readonly FakeHttpHandler _exchange = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => _db.Dispose();

    private DeltaSyncService Sync(bool granted = true) => new(
        new GraphClient(new HttpClient(new FakeHttpHandler()), new FakeAuthService(), NullLogger<GraphClient>.Instance),
        _db, new SyncOptions(), NullLogger<DeltaSyncService>.Instance, _time,
        MailboxSummaryTests.Service(_exchange, granted));

    [Fact]
    public async Task Refresh_CachesEveryRecipient_AndRecordsWhen()
    {
        _exchange.Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/users")).Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mailboxes"));

        await Sync().RefreshMailboxesAsync(CancellationToken.None);

        _db.Count(CacheEntity.Mailboxes).Should().Be(15);
        _db.GetMailbox("00000000-0000-0000-0000-000000000115")!.Kind.Should().Be(MailboxKind.Shared);
        _db.GetMailbox("00000000-0000-0000-0000-000000000165")!.Kind.Should().Be(MailboxKind.None);
        _db.GetMailbox("not-cached").Should().BeNull("a user Exchange has not been asked about is unknown, not mailbox-less");
        _db.GetSyncState(CacheEntity.Mailboxes)!.LastFullSync.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task Refresh_DropsRecipientsThatAreGone()
    {
        _exchange.Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/users")).Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mailboxes"));
        var sync = Sync();
        await sync.RefreshMailboxesAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(10));
        _exchange
            .Enqueue(HttpStatusCode.OK, """{"value":[{"ExternalDirectoryObjectId":"00000000-0000-0000-0000-000000000115","RecipientTypeDetails":"SharedMailbox"}]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        await sync.RefreshMailboxesAsync(CancellationToken.None);

        _db.Count(CacheEntity.Mailboxes).Should().Be(1);
    }

    [Fact]
    public async Task WithoutTheExchangeGrant_NothingIsCalled_AndNothingFails()
    {
        var sync = Sync(granted: false);

        await sync.RefreshMailboxesAsync(CancellationToken.None);

        _exchange.Requests.Should().BeEmpty();
        _db.GetSyncState(CacheEntity.Mailboxes).Should().BeNull();
        sync.LastError.Should().BeNull();
    }

    [Fact]
    public async Task AnAdminWithNoExchangeRole_IsNotASyncError()
    {
        _exchange.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"Access denied"}}""");
        var sync = Sync();

        // An exception escaping here is what the cycle's guard would record as LastError and show as a red sync pill.
        var act = () => sync.RefreshMailboxesAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a 403 is a permission state, not a sync failure");
        _db.Count(CacheEntity.Mailboxes).Should().Be(0);
        _db.GetSyncState(CacheEntity.Mailboxes).Should().BeNull("nothing was read, so the mailbox chips must stay hidden");
    }

    [Fact]
    public async Task AnyOtherExchangeFailure_IsStillReported()
    {
        _exchange.Enqueue(HttpStatusCode.BadRequest, Fixtures.Text("exchange/error-bad-parameter"));

        var act = () => Sync().RefreshMailboxesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ExchangeException>("only 'not permitted' is quiet; a real failure must surface");
    }
}
