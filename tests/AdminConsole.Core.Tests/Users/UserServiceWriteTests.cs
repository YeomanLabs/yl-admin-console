using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Tests.Fakes;
using AdminConsole.Core.Users;

namespace AdminConsole.Core.Tests.Users;

public sealed class UserServiceWriteTests : IDisposable
{
    private const string Id = "u-1";
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly UserService _sut;

    public UserServiceWriteTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new UserService(graph, _db, _actions, _tenant, NullLogger<UserService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    private void SeedUser(bool synced = false, string? jobTitle = "Analyst") =>
        _db.Write(w => w.UpsertUser(DirectoryUser.FromJson(Fixtures.Parse($$"""
            {"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,
             "jobTitle":{{(jobTitle is null ? "null" : $"\"{jobTitle}\"")}},"department":null,"businessPhones":["+1 425 555 0100"],
             "onPremisesSyncEnabled":{{synced.ToString().ToLowerInvariant()}},"assignedLicenses":[]}
            """))));

    [Fact]
    public async Task Update_PatchesOnlyTheChangedProperty_WritesThrough_AndLogsWithInverse()
    {
        SeedUser();
        _handler
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"jobTitle":"Engineer","assignedLicenses":[]}""");

        var r = await _sut.UpdateAsync(Id, "jobTitle", "Engineer", CancellationToken.None);

        r.Success.Should().BeTrue();
        r.Summary.Should().Be("Set job title on Ada Lovelace · was \"Analyst\"");
        r.CanUndo.Should().BeTrue();

        _handler.Requests[0].Method.Should().Be(HttpMethod.Patch);
        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/users/u-1");
        _handler.Requests[0].Body.Should().Be("""{"jobTitle":"Engineer"}""", "a minimal PATCH of just the changed property");
        _handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        _db.GetUser(Id)!.JobTitle.Should().Be("Engineer", "write-through means the UI is right before the next delta");

        var logged = _actions.Query(new ActionLogQuery { TargetId = Id }).Single();
        logged.Action.Should().Be("user.patch.jobTitle");
        logged.Outcome.Should().Be(ActionOutcome.Success);
        logged.Actor.Should().Be("admin@contoso.com");
        logged.TenantId.Should().Be("t-1");
        logged.TargetName.Should().Be("Ada Lovelace");
        logged.IsReversible.Should().BeTrue();
        logged.InversePayload.Should().Be("""{"jobTitle":"Analyst"}""");
    }

    [Fact]
    public async Task Undo_RestoresThePreviousValue_AndMarksTheOriginalUndone()
    {
        SeedUser();
        _handler
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","userPrincipalName":"ada@contoso.com","accountEnabled":true,"jobTitle":"Engineer","assignedLicenses":[]}""")
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","userPrincipalName":"ada@contoso.com","accountEnabled":true,"jobTitle":"Analyst","assignedLicenses":[]}""");

        var r = await _sut.UpdateAsync(Id, "jobTitle", "Engineer", CancellationToken.None);
        var undone = await r.Undo!(CancellationToken.None);

        undone.Success.Should().BeTrue();
        undone.Summary.Should().Be("Restored job title on Ada Lovelace");
        _handler.Requests[2].Body.Should().Be("""{"jobTitle":"Analyst"}""");
        _db.GetUser(Id)!.JobTitle.Should().Be("Analyst");
        _actions.Get(r.ActionId)!.UndoneAt.Should().NotBeNull("an undone action stops offering Undo");
        _actions.Query(new ActionLogQuery { Reversible = true }).Should().ContainSingle("only the undo's own row is still reversible");
    }

    [Fact]
    public async Task Update_OnPremMasteredProperty_IsRefusedWithoutCallingGraph_AndLoggedAsSkipped()
    {
        SeedUser(synced: true);

        var r = await _sut.UpdateAsync(Id, "jobTitle", "Engineer", CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be(DirectoryUser.HybridReadOnlyReason);
        _handler.Requests.Should().BeEmpty("the service refuses before Graph is called");
        var logged = _actions.Query(new ActionLogQuery { TargetId = Id }).Single();
        logged.Outcome.Should().Be(ActionOutcome.Skipped);
        logged.IsReversible.Should().BeFalse();
    }

    [Fact]
    public async Task Update_AlwaysWritableProperty_IsAllowedEvenOnASyncedUser()
    {
        SeedUser(synced: true);
        _handler
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","userPrincipalName":"ada@contoso.com","accountEnabled":true,"usageLocation":"GB","assignedLicenses":[]}""");

        var r = await _sut.UpdateAsync(Id, "usageLocation", "GB", CancellationToken.None);

        r.Success.Should().BeTrue("usageLocation is in the always-writable list (doc 05)");
        _handler.Requests[0].Body.Should().Be("""{"usageLocation":"GB"}""");
    }

    [Fact]
    public async Task Update_ClearingAProperty_SendsNull_NotAnEmptyString()
    {
        // Found by an offboarding run against the live tenant: the stamp step cleared officeLocation and department
        // with "", Graph accepted the first and rejected the second with "Invalid value specified for property
        // 'department'". Empty is not "no value" to Graph; null is. Same bug would hit an admin clearing a field on
        // the Identity tab.
        SeedUser();
        _handler
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"assignedLicenses":[]}""");

        var r = await _sut.UpdateAsync(Id, "jobTitle", string.Empty, CancellationToken.None);

        r.Success.Should().BeTrue();
        _handler.Requests[0].Body.Should().Be("""{"jobTitle":null}""",
            "an empty string is not how Graph is told to clear a property");
    }

    [Fact]
    public async Task Update_SameValue_IsANoOp_AndSendsNothing()
    {
        SeedUser();

        var r = await _sut.UpdateAsync(Id, "jobTitle", "Analyst", CancellationToken.None);

        r.Success.Should().BeTrue();
        r.WasNoOp.Should().BeTrue();
        r.CanUndo.Should().BeFalse();
        _handler.Requests.Should().BeEmpty();
        _actions.Count().Should().Be(0, "nothing happened, so nothing is logged");
    }

    [Fact]
    public async Task Update_ClearingAnAlreadyEmptyProperty_IsANoOp()
    {
        SeedUser(jobTitle: null);

        var r = await _sut.UpdateAsync(Id, "jobTitle", "", CancellationToken.None);

        r.WasNoOp.Should().BeTrue("null and empty are the same thing to an admin");
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_Arrays_RoundTripAndDiffCorrectly()
    {
        SeedUser();
        _handler
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","userPrincipalName":"ada@contoso.com","accountEnabled":true,"businessPhones":["+1 206 555 0111"],"assignedLicenses":[]}""");

        var same = await _sut.UpdateAsync(Id, "businessPhones", new[] { "+1 425 555 0100" }, CancellationToken.None);
        same.WasNoOp.Should().BeTrue();

        var changed = await _sut.UpdateAsync(Id, "businessPhones", new[] { "+1 206 555 0111" }, CancellationToken.None);
        changed.Success.Should().BeTrue();
        _handler.Requests[0].Body.Should().Be("""{"businessPhones":["+1 206 555 0111"]}""");
        _actions.Query(new ActionLogQuery { TargetId = Id }).Single().InversePayload.Should().Be("""{"businessPhones":["+1 425 555 0100"]}""");
    }

    [Fact]
    public async Task Update_GraphRejects_MapsToFriendlyText_LogsFailure_AndOffersNoUndo()
    {
        SeedUser();
        _handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges","innerError":{"request-id":"req-7"}}}""");

        var r = await _sut.UpdateAsync(Id, "jobTitle", "Engineer", CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("You don't have permission for this (User Administrator).");
        r.Details.Should().Contain("req-7").And.Contain("Authorization_RequestDenied");
        r.CanUndo.Should().BeFalse();
        var logged = _actions.Query(new ActionLogQuery { TargetId = Id }).Single();
        logged.Outcome.Should().Be(ActionOutcome.Failed);
        logged.GraphRequestId.Should().Be("req-7");
        _db.GetUser(Id)!.JobTitle.Should().Be("Analyst", "the cache is untouched when the write fails");
    }

    [Fact]
    public async Task Update_WriteThroughFailure_StillReportsSuccess()
    {
        SeedUser();
        _handler
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound","message":"gone"}}""");

        var r = await _sut.UpdateAsync(Id, "jobTitle", "Engineer", CancellationToken.None);

        r.Success.Should().BeTrue("the PATCH succeeded; a stale cache row is corrected by the next delta");
        _actions.Query(new ActionLogQuery { TargetId = Id }).Single().Outcome.Should().Be(ActionOutcome.Success);
    }

    [Theory]
    [InlineData("Request_ResourceNotFound", "gone", "That object no longer exists. The cache will update on next sync.")]
    [InlineData("Request_BadRequest", "usageLocation is required", "Set a usage location before assigning a license.")]
    [InlineData("Request_BadRequest", "There are no available licenses", "No seats left for this licence.")]
    [InlineData("Request_BadRequest", "Subscription with SKU 3ab6abff-666f-4424-bfb7-f0bc274ec7bc does not have any available licenses.", "No seats left for this licence.")]
    [InlineData("Request_BadRequest", "Unable to update the specified properties for on-premises mastered Directory Sync objects", "This property is mastered in on-prem AD and can't be changed here.")]
    public void ErrorText_MapsTheUxSection7Cases(string code, string message, string expected)
    {
        GraphErrorText.Friendly(new GraphException(400, code, message)).Should().Be(expected);
    }

    [Fact]
    public void ErrorText_ThrottleAndNetworkCases()
    {
        GraphErrorText.Friendly(new GraphException(429, "TooManyRequests", "slow down", retryAfter: TimeSpan.FromSeconds(12)))
            .Should().Be("Microsoft Graph is throttling requests. Retrying in 12 s.");
        GraphErrorText.Friendly(new GraphException(0, GraphException.NetworkErrorCode, "dns"))
            .Should().Be("Can't reach Microsoft Graph. Working from cached data.");
    }

    [Fact]
    public async Task Update_NeverLogsASecretEvenIfOneIsPassed()
    {
        SeedUser();
        _handler
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","userPrincipalName":"ada@contoso.com","accountEnabled":true,"assignedLicenses":[]}""");

        await _sut.UpdateAsync(Id, "passwordProfile", JsonSerializer.Deserialize<object>("""{"password":"Hunter2!"}"""), CancellationToken.None);

        var logged = _actions.Query(new ActionLogQuery { TargetId = Id }).Single();
        logged.RequestSummary.Should().NotContain("Hunter2");
        logged.InversePayload.Should().NotContain("Hunter2");
    }
}
