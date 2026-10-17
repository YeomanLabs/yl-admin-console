using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Tests.Fakes;
using AdminConsole.Core.Users;
using AdminConsole.Core.Users.BulkEdit;

namespace AdminConsole.Core.Tests.Users;

/// <summary>M18.3: the same fields on many people, each written where it is mastered.</summary>
public sealed class BulkEditTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly FakeHttpHandler _http = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly FakeDirectoryService _directory = new();
    private readonly BulkEditService _sut;
    private readonly OnPremUser _adCy;

    public BulkEditTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var graph = new GraphClient(new HttpClient(_http), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        var users = new UserService(graph, _db, _actions, _tenant, NullLogger<UserService>.Instance);
        _sut = new BulkEditService(users, _db, _directory, _actions, _tenant, NullLogger<BulkEditService>.Instance);

        _adCy = _directory.AddUser("cy", displayName: "Cy Synced");
        var anchor = Convert.ToBase64String(_adCy.ObjectGuid.ToByteArray());
        _db.Write(w =>
        {
            w.UpsertUser(User("u-ada", "Ada Cloud", "Engineering", synced: false));
            w.UpsertUser(User("u-bea", "Bea Cloud", "Sales", synced: false));
            w.UpsertUser(User("u-cy", "Cy Synced", "Engineering", synced: true, anchor: anchor));
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    private static DirectoryUser User(string id, string name, string department, bool synced, string? anchor = null) =>
        DirectoryUser.FromJson(Fixtures.Parse($$"""
            {"id":"{{id}}","displayName":"{{name}}","userPrincipalName":"{{id}}@contoso.com","accountEnabled":true,
             "department":"{{department}}","onPremisesSyncEnabled":{{(synced ? "true" : "false")}},
             "onPremisesImmutableId":{{(anchor is null ? "null" : $"\"{anchor}\"")}},"assignedLicenses":[]}
            """));

    [Fact]
    public void Plan_RoutesEachPropertyByTheHybridRule_AndLeavesAlreadyRightAlone()
    {
        var plan = _sut.Plan(["u-ada", "u-bea", "u-cy"], [new BulkChange("department", "Sales"), new BulkChange("usageLocation", "gb")]);

        plan.Problems.Should().BeEmpty();
        var ada = plan.Rows.Single(r => r.User.Id == "u-ada");
        ada.ToEntra.Select(c => c.Property).Should().BeEquivalentTo(["department", "usageLocation"]);

        var bea = plan.Rows.Single(r => r.User.Id == "u-bea");
        bea.Changes.Single(c => c.Property == "department").Route.Should().Be(BulkRoute.Unchanged, "Bea is already in Sales");

        var cy = plan.Rows.Single(r => r.User.Id == "u-cy");
        cy.ToDirectory.Select(c => c.Property).Should().Equal(["department"], "a synced user's department is mastered in AD");
        cy.ToEntra.Select(c => c.Property).Should().Equal(["usageLocation"], "usage location is cloud-mastered even when synced");
        cy.ToEntra.Single().To.Should().Be("GB", "the code is normalised before it is sent");
    }

    [Fact]
    public void Plan_ASyncedUsersAdField_IsWrittenEvenWhenTheCloudCopyAlreadyMatches()
    {
        // The cloud copy trails AD by up to a sync cycle: Cy's AD department may already have been changed away from
        // Engineering, and correcting it back must not be skipped because the stale cloud copy still says Engineering.
        var plan = _sut.Plan(["u-cy"], [new BulkChange("department", "Engineering")]);

        var change = plan.Rows.Single().Changes.Single();
        change.Route.Should().Be(BulkRoute.ActiveDirectory);
        change.Text.Should().Contain("AD can be ahead of it");
    }

    [Fact]
    public void Plan_RefusesAUsageLocationThatIsNotACountryCode()
    {
        var plan = _sut.Plan(["u-ada"], [new BulkChange("usageLocation", "United Kingdom")]);

        plan.Problems.Should().ContainSingle().Which.Should().Contain("two-letter");
        plan.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Plan_RefusesAPropertyThatIsNotOnTheBulkSheet()
    {
        _sut.Plan(["u-ada"], [new BulkChange("userPrincipalName", "x@contoso.com")]).Problems.Should().ContainSingle();
    }

    [Fact]
    public async Task Run_WritesCloudUsersByGraph_SyncedUsersInAd_AllUnderOneCorrelationId()
    {
        // Ada: one PATCH for both properties and the write-through re-read. Cy: the usage-location PATCH and its re-read.
        _http.Responder = req =>
        {
            if (req.Method == HttpMethod.Patch) return FakeHttpHandler.Json(HttpStatusCode.NoContent);
            var id = req.RequestUri!.AbsolutePath.Split('/').Last();
            return FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{id}}","displayName":"x","userPrincipalName":"{{id}}@contoso.com","accountEnabled":true,"onPremisesSyncEnabled":false,"assignedLicenses":[]}""");
        };
        var plan = _sut.Plan(["u-ada", "u-bea", "u-cy"], [new BulkChange("department", "Sales"), new BulkChange("usageLocation", "GB")]);

        var results = await _sut.RunAsync(plan, "run-42", null, Ct);

        results.Should().OnlyContain(r => r.Status == BulkStatus.Changed);
        var patches = _http.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        patches.Should().HaveCount(3, "one PATCH per person, not one per property");
        patches.Single(r => r.Uri.AbsolutePath.EndsWith("u-ada")).Body.Should().Contain("\"department\":\"Sales\"").And.Contain("\"usageLocation\":\"GB\"");
        patches.Single(r => r.Uri.AbsolutePath.EndsWith("u-cy")).Body.Should().NotContain("department", "Graph would refuse it and Entra Connect would revert it");
        patches.Single(r => r.Uri.AbsolutePath.EndsWith("u-bea")).Body.Should().NotContain("department", "Bea was already in Sales");

        (await _directory.GetUserAsync(_adCy.ObjectGuid, Ct))!.Department.Should().Be("Sales", "the synced user's department went to AD");

        var logged = _actions.Query(new ActionLogQuery { CorrelationId = "run-42" });
        logged.Should().HaveCount(4, "three Graph patches and one AD write, all tied to the run");
        logged.Should().ContainSingle(a => a.Action == "onprem.user.setAttribute");
    }

    [Fact]
    public async Task Run_ADownDomainController_FailsTheAdHalf_AndSaysPartlyDoneWhenTheCloudHalfLanded()
    {
        _directory.IsConnected = false;
        _http.Responder = req => req.Method == HttpMethod.Patch
            ? FakeHttpHandler.Json(HttpStatusCode.NoContent)
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"id":"u-cy","displayName":"Cy","userPrincipalName":"u-cy@contoso.com","accountEnabled":true,"onPremisesSyncEnabled":true,"assignedLicenses":[]}""");
        var plan = _sut.Plan(["u-cy"], [new BulkChange("department", "Sales"), new BulkChange("usageLocation", "GB")]);

        var result = (await _sut.RunAsync(plan, "run-43", null, Ct)).Single();

        result.Status.Should().Be(BulkStatus.PartlyDone);
        result.Detail.Should().StartWith("AD:", "what failed is said first");
    }

    [Fact]
    public async Task Run_NothingToChange_SendsNothing()
    {
        var plan = _sut.Plan(["u-bea"], [new BulkChange("department", "Sales")]);

        var result = (await _sut.RunAsync(plan, "run-44", null, Ct)).Single();

        result.Status.Should().Be(BulkStatus.NothingToDo);
        _http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_AGraphRefusal_IsThatPersonsFailure_NotTheRunsEnd()
    {
        _http.Responder = req => req.RequestUri!.AbsolutePath.EndsWith("u-ada") && req.Method == HttpMethod.Patch
            ? FakeHttpHandler.Json(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""")
            : req.Method == HttpMethod.Patch
                ? FakeHttpHandler.Json(HttpStatusCode.NoContent)
                : FakeHttpHandler.Json(HttpStatusCode.OK, """{"id":"u-bea","displayName":"Bea","userPrincipalName":"u-bea@contoso.com","accountEnabled":true,"onPremisesSyncEnabled":false,"assignedLicenses":[]}""");
        var plan = _sut.Plan(["u-ada", "u-bea"], [new BulkChange("jobTitle", "Analyst")]);

        var results = await _sut.RunAsync(plan, "run-45", null, Ct);

        results.Single(r => r.UserId == "u-ada").Status.Should().Be(BulkStatus.Failed);
        results.Single(r => r.UserId == "u-bea").Status.Should().Be(BulkStatus.Changed);
    }
}
