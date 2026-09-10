using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Sync;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Sync;

public sealed class DeltaSyncServiceTests : IDisposable
{
    private const string UsersDelta1 = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=U1";
    private const string UsersDelta2 = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=U2";
    private const string GroupsDelta1 = "https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G1";
    private const string DevicesDelta1 = "https://graph.microsoft.com/v1.0/devices/delta?$deltatoken=D1";

    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly FakeTimeProvider _time = new();
    private readonly List<SyncProgress> _events = [];
    private readonly DeltaSyncService _sut;

    public DeltaSyncServiceTests()
    {
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new DeltaSyncService(graph, _db, new SyncOptions { BatchSize = 2 }, NullLogger<DeltaSyncService>.Instance, _time);
        _sut.ProgressChanged += (_, p) => _events.Add(p);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Users_InitialWalkOfTwoPages_ThenDeltaWithUpdateAndRemoval()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, "3")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[{{User("u1", "Ada")}},{{User("u2", "Grace")}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=p2"}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[{{User("u3", "Linus")}}],"@odata.deltaLink":"{{UsersDelta1}}"}""");

        await _sut.SyncEntityAsync(CacheEntity.Users, CancellationToken.None);

        _db.Count(CacheEntity.Users).Should().Be(3);
        var state = _db.GetSyncState(CacheEntity.Users)!;
        state.DeltaLink.Should().Be(UsersDelta1);
        state.LastFullSync.Should().Be(_time.GetUtcNow());
        state.LastDeltaSync.Should().BeNull();
        state.RowCount.Should().Be(3);
        _sut.IsInitialSyncComplete.Should().BeFalse("groups and devices have no delta link yet");

        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/users/$count");
        _handler.Requests[0].Headers.Should().ContainKey("ConsistencyLevel");
        _handler.Requests[1].Uri.PathAndQuery.Should().StartWith("/v1.0/users/delta?$select=id,displayName").And.Contain("$top=999").And.NotContain("signInActivity");
        _handler.Requests[2].Uri.Query.Should().Contain("skiptoken=p2");

        var last = _events.Last();
        last.Entity.Should().Be(CacheEntity.Users);
        last.Phase.Should().Be(SyncPhase.Initial);
        last.IsDone.Should().BeTrue();
        last.Processed.Should().Be(3);
        last.Total.Should().Be(3);
        last.Fraction.Should().Be(1.0);
        // One event per batch of BatchSize=2, plus the opening zero.
        _events.Where(e => !e.IsDone).Select(e => e.Processed).Should().Equal(0, 2, 3);

        // --- delta tick: u2 renamed, u3 removed ---
        _events.Clear();
        _time.Advance(TimeSpan.FromMinutes(2));
        _handler.Enqueue(HttpStatusCode.OK, $$$"""{"value":[{{{User("u2", "Grace Hopper")}}},{"id":"u3","@removed":{"reason":"deleted"}}],"@odata.deltaLink":"{{{UsersDelta2}}}"}""");

        await _sut.SyncEntityAsync(CacheEntity.Users, CancellationToken.None);

        _handler.Requests[3].Uri.Should().Be(new Uri(UsersDelta1));
        _handler.Requests.Should().HaveCount(4, "no $count on a delta tick");
        _db.Count(CacheEntity.Users).Should().Be(2);
        _db.GetRaw(CacheEntity.Users, "u2").Should().Contain("Grace Hopper");
        _db.GetRaw(CacheEntity.Users, "u3").Should().BeNull();
        state = _db.GetSyncState(CacheEntity.Users)!;
        state.DeltaLink.Should().Be(UsersDelta2);
        state.LastDeltaSync.Should().Be(_time.GetUtcNow());
        state.LastFullSync.Should().Be(_time.GetUtcNow() - TimeSpan.FromMinutes(2), "the initial-walk stamp is preserved");
        state.RowCount.Should().Be(2);
        _events.Last().Phase.Should().Be(SyncPhase.Delta);
        _events.Last().Total.Should().BeNull();
    }

    [Fact]
    public async Task Groups_InitialWalkSeedsMembers_ThenDeltaAddsAndRemovesMembers()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, "1")
            .Enqueue(HttpStatusCode.OK, $$"""
                {"value":[{"id":"g1","displayName":"Sales","securityEnabled":true,"mailEnabled":false,"groupTypes":[],
                  "members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u1"},{"@odata.type":"#microsoft.graph.user","id":"u2"},{"@odata.type":"#microsoft.graph.device","id":"d1"}]}],
                 "@odata.deltaLink":"{{GroupsDelta1}}"}
                """);

        await _sut.SyncEntityAsync(CacheEntity.Groups, CancellationToken.None);

        Members("g1").Should().BeEquivalentTo([("u1", "user"), ("u2", "user"), ("d1", "device")]);
        MemberCount("g1").Should().Be(3);
        _handler.Requests[1].Uri.Query.Should().Contain(",members&");

        _handler.Enqueue(HttpStatusCode.OK, $$$"""
            {"value":[{"id":"g1","displayName":"Sales","securityEnabled":true,"mailEnabled":false,"groupTypes":[],
              "members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u3"},{"@odata.type":"#microsoft.graph.user","id":"u1","@removed":{"reason":"deleted"}}]}],
             "@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G2"}
            """);

        await _sut.SyncEntityAsync(CacheEntity.Groups, CancellationToken.None);

        Members("g1").Select(m => m.Id).Should().BeEquivalentTo(["u2", "d1", "u3"]);
        MemberCount("g1").Should().Be(3);
        _db.GetSyncState(CacheEntity.Groups)!.DeltaLink.Should().EndWith("G2");
    }

    [Fact]
    public async Task Groups_RemovedGroup_DropsItsMemberships()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, "1")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[{"id":"g1","securityEnabled":true,"mailEnabled":false,"groupTypes":[],"members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u1"}]}],"@odata.deltaLink":"{{GroupsDelta1}}"}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"g1","@removed":{"reason":"deleted"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G2"}""");

        await _sut.SyncEntityAsync(CacheEntity.Groups, CancellationToken.None);
        await _sut.SyncEntityAsync(CacheEntity.Groups, CancellationToken.None);

        _db.Count(CacheEntity.Groups).Should().Be(0);
        Members("g1").Should().BeEmpty();
    }

    [Fact]
    public async Task Delta410_ClearsEntityAndRewalks()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, "2")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[{{User("u1", "Ada")}},{{User("u2", "Grace")}}],"@odata.deltaLink":"{{UsersDelta1}}"}""")
            .Enqueue(HttpStatusCode.Gone, """{"error":{"code":"syncStateNotFound","message":"Sync state not found. Perform a full sync."}}""")
            .Enqueue(HttpStatusCode.OK, "1")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[{{User("u9", "Fresh")}}],"@odata.deltaLink":"{{UsersDelta2}}"}""");

        await _sut.SyncEntityAsync(CacheEntity.Users, CancellationToken.None);
        await _sut.SyncEntityAsync(CacheEntity.Users, CancellationToken.None);

        _db.Count(CacheEntity.Users).Should().Be(1);
        _db.GetRaw(CacheEntity.Users, "u9").Should().NotBeNull();
        _db.GetRaw(CacheEntity.Users, "u1").Should().BeNull();
        _db.GetSyncState(CacheEntity.Users)!.DeltaLink.Should().Be(UsersDelta2);
        _handler.Requests.Select(r => r.Uri.AbsolutePath).Should().Equal(
            "/v1.0/users/$count", "/v1.0/users/delta", "/v1.0/users/delta", "/v1.0/users/$count", "/v1.0/users/delta");
        _handler.Requests[2].Uri.Should().Be(new Uri(UsersDelta1));
        _events.Where(e => e.Phase == SyncPhase.Initial && e.IsDone).Should().HaveCount(2);
    }

    [Fact]
    public async Task ManagedDevices_RefreshUpsertsAndDeletesStaleRows()
    {
        _db.Write(w =>
        {
            w.UpsertManagedDevice(new ManagedDevice { Id = "m1", DeviceName = "OLD-1", Raw = "{}" });
            w.UpsertManagedDevice(new ManagedDevice { Id = "m2", DeviceName = "OLD-2", Raw = "{}" });
        });
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"m1","deviceName":"NEW-1","complianceState":"compliant","userId":"u1"}]}""");

        await _sut.RefreshManagedDevicesAsync(CancellationToken.None);

        _db.Count(CacheEntity.ManagedDevices).Should().Be(1);
        _db.GetRaw(CacheEntity.ManagedDevices, "m1").Should().Contain("NEW-1");
        _db.GetRaw(CacheEntity.ManagedDevices, "m2").Should().BeNull("not returned by the walk → retired");
        _handler.Requests[0].Uri.PathAndQuery.Should().StartWith("/v1.0/deviceManagement/managedDevices?$select=id,deviceName").And.Contain("$top=1000");
        var state = _db.GetSyncState(CacheEntity.ManagedDevices)!;
        state.LastFullSync.Should().Be(_time.GetUtcNow());
        state.RowCount.Should().Be(1);
        _events.Last().Should().BeEquivalentTo(new { Entity = CacheEntity.ManagedDevices, Phase = SyncPhase.Refresh, Processed = 1, Total = 1, IsDone = true });
    }

    [Fact]
    public async Task Skus_RefreshReplacesTable()
    {
        _db.Write(w => w.UpsertSku(new LicenseSku { SkuId = "old", SkuPartNumber = "OLD", Enabled = 1, Consumed = 0, Raw = "{}" }));
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[{"skuId":"new","skuPartNumber":"SPE_E3","consumedUnits":2,"prepaidUnits":{"enabled":5},"capabilityStatus":"Enabled"}]}""");

        await _sut.RefreshSkusAsync(CancellationToken.None);

        _db.Count(CacheEntity.Skus).Should().Be(1);
        _db.GetRaw(CacheEntity.Skus, "new").Should().NotBeNull();
        _db.GetRaw(CacheEntity.Skus, "old").Should().BeNull();
    }

    [Fact]
    public async Task RunCycle_OrdersEntities_AndRefreshesNoDeltaEntitiesOnlyWhenDue()
    {
        EnqueueFullFirstCycle();
        await _sut.RunCycleAsync(force: false, CancellationToken.None);

        _handler.Requests.Select(r => r.Uri.AbsolutePath).Should().Equal(
            "/v1.0/users/$count", "/v1.0/users/delta",
            "/v1.0/groups/$count", "/v1.0/groups/delta",
            "/v1.0/devices/$count", "/v1.0/devices/delta",
            "/v1.0/deviceManagement/managedDevices", "/v1.0/subscribedSkus", "/v1.0/organization");
        _sut.IsInitialSyncComplete.Should().BeTrue();
        _sut.LastCycleCompleted.Should().Be(_time.GetUtcNow());
        _sut.IsRunning.Should().BeFalse();
        _db.Count(CacheEntity.Organization).Should().Be(1);

        // Second cycle two minutes later: deltas only, managed devices not yet due (600 s).
        _handler.Requests.Clear();
        _time.Advance(TimeSpan.FromMinutes(2));
        EnqueueDeltaTicks();
        await _sut.RunCycleAsync(force: false, CancellationToken.None);
        _handler.Requests.Select(r => r.Uri.AbsolutePath).Should().Equal("/v1.0/users/delta", "/v1.0/groups/delta", "/v1.0/devices/delta");

        // Eleven minutes later: due again.
        _handler.Requests.Clear();
        _time.Advance(TimeSpan.FromMinutes(11));
        EnqueueDeltaTicks();
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""").Enqueue(HttpStatusCode.OK, """{"value":[]}""").Enqueue(HttpStatusCode.OK, """{"value":[{"id":"t","displayName":"T"}]}""");
        await _sut.RunCycleAsync(force: false, CancellationToken.None);
        _handler.Requests.Should().HaveCount(6);

        // SyncNow forces the refresh regardless of the interval.
        _handler.Requests.Clear();
        EnqueueDeltaTicks();
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""").Enqueue(HttpStatusCode.OK, """{"value":[]}""").Enqueue(HttpStatusCode.OK, """{"value":[{"id":"t","displayName":"T"}]}""");
        await _sut.SyncNowAsync(CancellationToken.None);
        _handler.Requests.Should().HaveCount(6);
    }

    [Fact]
    public async Task RunCycle_OneEntityFailing_DoesNotStopTheOthers()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, "1")
            .Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"nope"}}""")
            .Enqueue(HttpStatusCode.OK, "1")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[{"id":"g1","securityEnabled":true,"mailEnabled":false,"groupTypes":[]}],"@odata.deltaLink":"{{GroupsDelta1}}"}""")
            .Enqueue(HttpStatusCode.OK, "0")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[],"@odata.deltaLink":"{{DevicesDelta1}}"}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"t","displayName":"T"}]}""");

        await _sut.RunCycleAsync(force: false, CancellationToken.None);

        _sut.LastError.Should().BeOfType<GraphException>().Which.StatusCode.Should().Be(403);
        _sut.Latest[CacheEntity.Users].Error.Should().NotBeNull();
        _sut.Latest[CacheEntity.Users].IsDone.Should().BeTrue();
        _db.Count(CacheEntity.Groups).Should().Be(1);
        _db.GetSyncState(CacheEntity.Users).Should().BeNull("nothing was committed for users");
        _sut.IsInitialSyncComplete.Should().BeFalse();
    }

    [Fact]
    public void MemberType_StripsTheODataPrefix()
    {
        DeltaSyncService.MemberType("#microsoft.graph.user").Should().Be("user");
        DeltaSyncService.MemberType("#microsoft.graph.servicePrincipal").Should().Be("servicePrincipal");
        DeltaSyncService.MemberType(null).Should().Be("unknown");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private void EnqueueFullFirstCycle()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, "1").Enqueue(HttpStatusCode.OK, $$"""{"value":[{{User("u1", "Ada")}}],"@odata.deltaLink":"{{UsersDelta1}}"}""")
            .Enqueue(HttpStatusCode.OK, "1").Enqueue(HttpStatusCode.OK, $$"""{"value":[{"id":"g1","securityEnabled":true,"mailEnabled":false,"groupTypes":[]}],"@odata.deltaLink":"{{GroupsDelta1}}"}""")
            .Enqueue(HttpStatusCode.OK, "1").Enqueue(HttpStatusCode.OK, $$"""{"value":[{"id":"e1","deviceId":"aad-1"}],"@odata.deltaLink":"{{DevicesDelta1}}"}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"m1","deviceName":"PC"}]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"skuId":"s1","skuPartNumber":"SPE_E3","consumedUnits":1,"prepaidUnits":{"enabled":2}}]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"t","displayName":"Tenant","assignedPlans":[]}]}""");
    }

    private void EnqueueDeltaTicks()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[],"@odata.deltaLink":"{{UsersDelta1}}"}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[],"@odata.deltaLink":"{{GroupsDelta1}}"}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[],"@odata.deltaLink":"{{DevicesDelta1}}"}""");
    }

    private static string User(string id, string name) =>
        $$"""{"id":"{{id}}","displayName":"{{name}}","userPrincipalName":"{{id}}@contoso.com","accountEnabled":true,"assignedLicenses":[]}""";

    private List<(string Id, string Type)> Members(string groupId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT member_id, member_type FROM group_members WHERE group_id=$g ORDER BY member_id;";
        cmd.Parameters.AddWithValue("$g", groupId);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    private long MemberCount(string groupId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT member_count FROM groups WHERE id=$g;";
        cmd.Parameters.AddWithValue("$g", groupId);
        return (long)cmd.ExecuteScalar()!;
    }
}
