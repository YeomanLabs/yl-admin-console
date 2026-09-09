using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Licensing;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Licensing;

public sealed class LicenseServiceTests : IDisposable
{
    private const string Id = "u-1";
    private const string Sku = "s-e3";
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly LicenseService _sut;

    public LicenseServiceTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new LicenseService(graph, _db, _actions, _tenant, NullLogger<LicenseService>.Instance,
            (d, _) => { _waits.Add(d); return Task.CompletedTask; });
        _db.Write(w => w.UpsertSku(new LicenseSku { SkuId = Sku, SkuPartNumber = "SPE_E3", FriendlyName = "Microsoft 365 E3", Enabled = 10, Consumed = 3, Raw = "{}" }));
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    private void SeedUser(string? usageLocation = "GB") =>
        _db.Write(w => w.UpsertUser(DirectoryUser.FromJson(Fixtures.Parse($$"""
            {"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,
             "usageLocation":{{(usageLocation is null ? "null" : $"\"{usageLocation}\"")}},"assignedLicenses":[]}
            """))));

    [Fact]
    public async Task Assign_PostsAssignLicense_WritesThrough_AndLogsWithInverse()
    {
        SeedUser();
        _handler
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1"}""")
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"usageLocation":"GB","assignedLicenses":[{"skuId":"s-e3"}]}""");

        var r = await _sut.AssignAsync(Id, Sku, CancellationToken.None);

        r.Success.Should().BeTrue();
        r.Summary.Should().Be("Added Microsoft 365 E3 to Ada Lovelace", "the toast uses the friendly SKU name, not the GUID");
        r.CanUndo.Should().BeTrue();
        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/users/u-1/assignLicense");
        _handler.Requests[0].Body.Should().Be("""{"addLicenses":[{"skuId":"s-e3","disabledPlans":[]}],"removeLicenses":[]}""");
        _db.GetUser(Id)!.Licensed.Should().BeTrue("write-through");

        var logged = _actions.Query(new ActionLogQuery { TargetId = Id }).Single();
        logged.Action.Should().Be("license.assign");
        logged.InverseAction.Should().Be("license.remove");
        logged.IsReversible.Should().BeTrue();
    }

    [Fact]
    public async Task Assign_WithoutUsageLocation_IsRefusedBeforeCallingGraph()
    {
        SeedUser(usageLocation: null);

        var r = await _sut.AssignAsync(Id, Sku, CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("Set a usage location before assigning a license.");
        _handler.Requests.Should().BeEmpty("Graph would only return a 400 saying the same thing");
        _actions.Query(new ActionLogQuery { TargetId = Id }).Single().Outcome.Should().Be(ActionOutcome.Skipped);
    }

    [Fact]
    public async Task Remove_PostsRemoveLicenses_AndUndoAddsItBack()
    {
        SeedUser();
        _handler
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1"}""")
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"usageLocation":"GB","assignedLicenses":[]}""")
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1"}""")
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"usageLocation":"GB","assignedLicenses":[{"skuId":"s-e3"}]}""");

        var r = await _sut.RemoveAsync(Id, Sku, CancellationToken.None);
        r.Summary.Should().Be("Removed Microsoft 365 E3 from Ada Lovelace");
        _handler.Requests[0].Body.Should().Be("""{"addLicenses":[],"removeLicenses":["s-e3"]}""");

        var undone = await r.Undo!(CancellationToken.None);

        undone.Success.Should().BeTrue();
        undone.Summary.Should().Be("Added Microsoft 365 E3 to Ada Lovelace");
        _handler.Requests[2].Body.Should().Contain("addLicenses").And.Contain("s-e3");
        _actions.Get(r.ActionId)!.UndoneAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Assign_NoSeatsLeft_MapsToTheFriendlySentence()
    {
        SeedUser();
        _handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"Request_BadRequest","message":"There are no available licenses (CountViolation)"}}""");

        var r = await _sut.AssignAsync(Id, Sku, CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("No seats left for this licence.");
        _actions.Query(new ActionLogQuery { TargetId = Id }).Single().Outcome.Should().Be(ActionOutcome.Failed);
    }

    [Fact]
    public async Task Assign_WaitsForGraphToConverge_BeforeCachingTheReRead()
    {
        // assignLicense is eventually consistent: the first read back can still show the old set. Caching that would
        // leave the UI showing the pre-write state until the next delta tick.
        SeedUser();
        const string stale = """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"usageLocation":"GB","assignedLicenses":[]}""";
        const string fresh = """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"usageLocation":"GB","assignedLicenses":[{"skuId":"s-e3"}]}""";
        _handler
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1"}""")
            .Enqueue(HttpStatusCode.OK, stale)
            .Enqueue(HttpStatusCode.OK, stale)
            .Enqueue(HttpStatusCode.OK, fresh);

        var r = await _sut.AssignAsync(Id, Sku, CancellationToken.None);

        r.Success.Should().BeTrue();
        _handler.Requests.Should().HaveCount(4, "it re-read until the licence appeared");
        _waits.Should().Equal(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800));
        _db.GetUser(Id)!.Licensed.Should().BeTrue("the cache holds the converged state, not the stale one");
    }

    [Fact]
    public async Task Assign_GivesUpConverging_ButStillCachesAndSucceeds()
    {
        SeedUser();
        const string stale = """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"usageLocation":"GB","assignedLicenses":[]}""";
        _handler.Enqueue(HttpStatusCode.OK, """{"id":"u-1"}""");
        for (var i = 0; i < LicenseService.MaxConvergenceAttempts; i++) _handler.Enqueue(HttpStatusCode.OK, stale);

        var r = await _sut.AssignAsync(Id, Sku, CancellationToken.None);

        r.Success.Should().BeTrue("the POST succeeded; only the read is lagging");
        _handler.Requests.Should().HaveCount(1 + LicenseService.MaxConvergenceAttempts, "it stops after a bounded number of reads");
        _waits.Should().HaveCount(LicenseService.MaxConvergenceAttempts - 1);
    }

    [Fact]
    public void GroupAssignedLicence_CannotBeRemovedPerUser()
    {
        var group = new LicenseAssignmentState { SkuId = Sku, AssignedByGroup = "g-1" };
        var direct = new LicenseAssignmentState { SkuId = Sku };

        LicenseService.RemovalBlockedReason(group).Should().Be("Assigned by a group — remove the user from that group instead.");
        LicenseService.RemovalBlockedReason(direct).Should().BeNull();
        LicenseService.RemovalBlockedReason(null).Should().BeNull();
    }

    [Fact]
    public void Skus_ComeFromTheCache_WithFriendlyNamesAndSeatCounts()
    {
        var sku = _sut.GetSku(Sku)!;

        sku.DisplayName.Should().Be("Microsoft 365 E3");
        sku.Available.Should().Be(7);
        _sut.GetSkus().Should().ContainSingle();
        _sut.GetSku("nope").Should().BeNull();
    }
}
