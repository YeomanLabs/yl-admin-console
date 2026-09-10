using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Templates;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Templates;

/// <summary>M7.3: the uniqueness checks and the four-step create of doc 07.</summary>
public sealed class UserProvisioningServiceTests : IDisposable
{
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly UserProvisioningService _sut;

    public UserProvisioningServiceTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new UserProvisioningService(graph, _db, _actions, _tenant, NullLogger<UserProvisioningService>.Instance, (_, _) => Task.CompletedTask);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    private static CreateUserRequest Request(string? managerId = null, IReadOnlyList<string>? skus = null, IReadOnlyList<TemplateGroup>? groups = null) => new()
    {
        DisplayName = "Jane Doe",
        GivenName = "Jane",
        Surname = "Doe",
        UserPrincipalName = "jane.doe@contoso.com",
        MailNickname = "jane.doe",
        Password = "Correct-Horse-42x",
        Attributes = new Dictionary<string, object?> { ["jobTitle"] = "Sales Representative", ["usageLocation"] = "US" },
        ManagerId = managerId,
        SkuIds = skus ?? [],
        Groups = groups ?? [],
    };

    private const string CreatedUser = """
        {"id":"u-new","displayName":"Jane Doe","userPrincipalName":"jane.doe@contoso.com","accountEnabled":true,"assignedLicenses":[]}
        """;

    // ---- uniqueness (doc 07 §flow step 2) ---------------------------------------------------------------------------

    [Fact]
    public async Task CheckUpn_AFreeName_IsAvailable_AndAsksGraphWithACountFilter()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"@odata.count":0,"value":[]}""");

        var result = await _sut.CheckUpnAsync("jane.doe@contoso.com", CancellationToken.None);

        result.State.Should().Be(UniquenessState.Available);
        result.BlocksCreate.Should().BeFalse();
        var request = _handler.Requests[0];
        Uri.UnescapeDataString(request.Uri.Query).Should().Contain("userPrincipalName eq 'jane.doe@contoso.com'");
        request.Headers.Should().ContainKey("ConsistencyLevel").WhoseValue.Should().Be("eventual");
    }

    [Fact]
    public async Task CheckUpn_ATakenName_BlocksTheCreate()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"@odata.count":1,"value":[{"id":"u-1"}]}""");

        var result = await _sut.CheckUpnAsync("jane.doe@contoso.com", CancellationToken.None);

        result.State.Should().Be(UniquenessState.Taken);
        result.BlocksCreate.Should().BeTrue();
        result.Message.Should().Be("That sign-in name is already in use.");
    }

    [Fact]
    public async Task CheckUpn_WhenGraphCannotAnswer_SaysSo_RatherThanClaimingItIsFree()
    {
        // Telling an admin a UPN is available when nobody checked is worse than admitting the check failed.
        _handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"no"}}""");

        var result = await _sut.CheckUpnAsync("jane.doe@contoso.com", CancellationToken.None);

        result.State.Should().Be(UniquenessState.Unknown);
        result.BlocksCreate.Should().BeFalse("an admin can still choose to go ahead");
        result.Message.Should().StartWith("Could not check");
    }

    [Fact]
    public async Task CheckUpn_AnIncompleteName_IsInvalid_WithoutCallingGraph()
    {
        var result = await _sut.CheckUpnAsync("jane.doe", CancellationToken.None);

        result.State.Should().Be(UniquenessState.Invalid);
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckProxyAddress_LooksForTheSmtpCollision()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"@odata.count":1,"value":[{"id":"u-1"}]}""");

        var result = await _sut.CheckProxyAddressAsync("jane.doe@contoso.com", CancellationToken.None);

        result.State.Should().Be(UniquenessState.Taken);
        Uri.UnescapeDataString(_handler.Requests[0].Uri.Query).Should().Contain("proxyAddresses/any(p:p eq 'SMTP:jane.doe@contoso.com')");
    }

    [Fact]
    public async Task Checks_EscapeAnApostrophe_SoAnOBrienDoesNotBreakTheFilter()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await _sut.CheckUpnAsync("o'brien@contoso.com", CancellationToken.None);

        Uri.UnescapeDataString(_handler.Requests[0].Uri.Query).Should().Contain("eq 'o''brien@contoso.com'");
    }

    // ---- create (doc 07 §flow step 4) -------------------------------------------------------------------------------

    [Fact]
    public async Task Create_PostsTheDocumentedBody_AndCachesTheNewUser()
    {
        _handler.Enqueue(HttpStatusCode.Created, CreatedUser);

        var result = await _sut.CreateAsync(Request(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.UserId.Should().Be("u-new");
        result.Password.Should().Be("Correct-Horse-42x", "the show-once screen is the only consumer");

        using var body = JsonDocument.Parse(_handler.Requests[0].Body!);
        var root = body.RootElement;
        root.GetProperty("accountEnabled").GetBoolean().Should().BeTrue();
        root.GetProperty("displayName").GetString().Should().Be("Jane Doe");
        root.GetProperty("userPrincipalName").GetString().Should().Be("jane.doe@contoso.com");
        root.GetProperty("mailNickname").GetString().Should().Be("jane.doe");
        root.GetProperty("jobTitle").GetString().Should().Be("Sales Representative");
        root.GetProperty("usageLocation").GetString().Should().Be("US");
        root.GetProperty("passwordProfile").GetProperty("forceChangePasswordNextSignIn").GetBoolean().Should().BeTrue();

        _db.GetUser("u-new").Should().NotBeNull("the new user must be findable before the next delta");
    }

    [Fact]
    public async Task Create_NeverLetsThePasswordReachTheActionLog()
    {
        _handler.Enqueue(HttpStatusCode.Created, CreatedUser);

        await _sut.CreateAsync(Request(), CancellationToken.None);

        var logged = _actions.Query(new ActionLogQuery { TargetId = "u-new" }).Single();
        logged.Action.Should().Be("user.create");
        (logged.RequestSummary + logged.Message).Should().NotContain("Correct-Horse");
    }

    [Fact]
    public async Task Create_ATemplateCannotOverrideTheIdentityFieldsTheFormOwns()
    {
        // A stale displayName left in a template file must not silently rename the person being created.
        _handler.Enqueue(HttpStatusCode.Created, CreatedUser);
        var request = Request() with
        {
            Attributes = new Dictionary<string, object?>
            {
                ["displayName"] = "Someone Else",
                ["userPrincipalName"] = "someone.else@contoso.com",
                ["jobTitle"] = "Sales Representative",
            },
        };

        await _sut.CreateAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(_handler.Requests[0].Body!);
        body.RootElement.GetProperty("displayName").GetString().Should().Be("Jane Doe");
        body.RootElement.GetProperty("userPrincipalName").GetString().Should().Be("jane.doe@contoso.com");
    }

    [Fact]
    public async Task Create_WhenTheAccountCannotBeCreated_EverythingAfterItIsNotRun()
    {
        _handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"Request_BadRequest","message":"Another object with the same value for property userPrincipalName already exists."}}""");

        var result = await _sut.CreateAsync(Request(managerId: "m-1", skus: ["sku-1"]), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Password.Should().BeNull("no account exists to have a password");
        result.Steps.Should().HaveCount(4);
        result.Steps[0].State.Should().Be(StepState.Failed);
        result.Steps.Skip(1).Should().OnlyContain(s => s.State == StepState.NotRun);
        _handler.Requests.Should().ContainSingle("nothing is attempted against an account that is not there");
    }

    [Fact]
    public async Task Create_AssignsLicences_AddsGroupsInOneBatch_ThenSetsTheManager()
    {
        _db.Write(w => w.UpsertSku(new LicenseSku { SkuId = "sku-1", SkuPartNumber = "SPE_E3", FriendlyName = "Microsoft 365 E3", Enabled = 10, Consumed = 1, Raw = "{}" }));
        _handler
            .Enqueue(HttpStatusCode.Created, CreatedUser)
            .Enqueue(HttpStatusCode.OK, CreatedUser)
            .Enqueue(HttpStatusCode.OK, """{"responses":[{"id":"g-1","status":204},{"id":"g-2","status":204}]}""")
            .Enqueue(HttpStatusCode.NoContent);

        var result = await _sut.CreateAsync(Request(
            managerId: "m-1",
            skus: ["sku-1"],
            groups: [new TemplateGroup { Id = "g-1", DisplayName = "Sales-Team" }, new TemplateGroup { Id = "g-2", DisplayName = "All-Staff" }]),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Steps.Select(s => s.State).Should().AllBeEquivalentTo(StepState.Success);
        result.Steps[1].Message.Should().Be("Assigned 1 licence: Microsoft 365 E3");
        result.Steps[2].Message.Should().Be("Added to 2 groups: Sales-Team, All-Staff");

        _handler.Requests[1].Uri.AbsolutePath.Should().Be("/v1.0/users/u-new/assignLicense");
        _handler.Requests[2].Uri.AbsolutePath.Should().Be("/v1.0/$batch", "group adds go in one round trip");
        _handler.Requests[3].Method.Should().Be(HttpMethod.Put);
        _handler.Requests[3].Uri.AbsolutePath.Should().Be("/v1.0/users/u-new/manager/$ref");
    }

    [Fact]
    public async Task Create_ALaterFailureDoesNotUndoTheAccount_ItIsReported()
    {
        // A user missing one group is a smaller problem than a user who half exists.
        _handler
            .Enqueue(HttpStatusCode.Created, CreatedUser)
            .Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"Request_BadRequest","message":"There are no available licenses"}}""")
            .Enqueue(HttpStatusCode.OK, """{"responses":[{"id":"g-1","status":403}]}""")
            .Enqueue(HttpStatusCode.NoContent);

        var result = await _sut.CreateAsync(Request(managerId: "m-1", skus: ["sku-1"], groups: [new TemplateGroup { Id = "g-1", DisplayName = "Sales-Team" }]), CancellationToken.None);

        result.Success.Should().BeTrue("the account exists, which is what Success means here");
        result.UserId.Should().Be("u-new");
        result.Steps[1].State.Should().Be(StepState.Failed);
        result.Steps[2].State.Should().Be(StepState.Failed);
        result.Steps[3].State.Should().Be(StepState.Success);
        result.Summary.Should().Be("2 completed · 2 failed · 0 s");
    }

    [Fact]
    public async Task Create_ALicenceRaceWithDirectoryReplication_IsRetried_NotReportedAsAFailure()
    {
        // Verified on the dev tenant: Entra answers 409 Directory_ConcurrencyViolation when a licence is assigned too
        // soon after the account is created, and its own message says to wait briefly and retry.
        const string conflict = """{"error":{"code":"Directory_ConcurrencyViolation","message":"Error due to concurrent requests being made to the tenant. Please wait briefly and retry."}}""";
        _handler
            .Enqueue(HttpStatusCode.Created, CreatedUser)
            .Enqueue(HttpStatusCode.Conflict, conflict)
            .Enqueue(HttpStatusCode.Conflict, conflict)
            .Enqueue(HttpStatusCode.OK, CreatedUser);

        var result = await _sut.CreateAsync(Request(skus: ["sku-1"]), CancellationToken.None);

        result.Steps[1].State.Should().Be(StepState.Success);
        _handler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/assignLicense", StringComparison.Ordinal)).Should().Be(3);
    }

    [Fact]
    public async Task Create_ALicenceRaceThatNeverSettles_GivesUpAndSaysSo()
    {
        const string conflict = """{"error":{"code":"Directory_ConcurrencyViolation","message":"Error due to concurrent requests being made to the tenant. Please wait briefly and retry."}}""";
        _handler.Enqueue(HttpStatusCode.Created, CreatedUser);
        for (var i = 0; i < UserProvisioningService.MaxLicenceAttempts; i++) _handler.Enqueue(HttpStatusCode.Conflict, conflict);

        var result = await _sut.CreateAsync(Request(skus: ["sku-1"]), CancellationToken.None);

        result.Steps[1].State.Should().Be(StepState.Failed);
        result.Success.Should().BeTrue("the account exists either way");
    }

    [Fact]
    public async Task Create_ARealConflictIsNotRetried()
    {
        _handler
            .Enqueue(HttpStatusCode.Created, CreatedUser)
            .Enqueue(HttpStatusCode.Conflict, """{"error":{"code":"Request_BadRequest","message":"Conflicting objects"}}""");

        var result = await _sut.CreateAsync(Request(skus: ["sku-1"]), CancellationToken.None);

        result.Steps[1].State.Should().Be(StepState.Failed);
        _handler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/assignLicense", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Create_WithNoUsageLocation_SkipsTheLicenceStepWithTheReason()
    {
        _handler.Enqueue(HttpStatusCode.Created, CreatedUser);
        var request = Request(skus: ["sku-1"]) with { Attributes = new Dictionary<string, object?> { ["jobTitle"] = "Sales" } };

        var result = await _sut.CreateAsync(request, CancellationToken.None);

        result.Steps[1].State.Should().Be(StepState.Skipped);
        result.Steps[1].Message.Should().Contain("usage location");
        _handler.Requests.Should().ContainSingle("Graph would refuse it anyway; refusing locally is faster and clearer");
    }

    [Fact]
    public async Task Create_WithNothingButTheAccount_SkipsTheOtherThreeSteps()
    {
        _handler.Enqueue(HttpStatusCode.Created, CreatedUser);

        var result = await _sut.CreateAsync(Request(), CancellationToken.None);

        result.Steps.Skip(1).Should().OnlyContain(s => s.State == StepState.Skipped);
        result.Summary.Should().StartWith("1 completed");
    }

    [Fact]
    public async Task Create_GroupAdds_AreLoggedAgainstTheGroup_NotTheUser()
    {
        _handler
            .Enqueue(HttpStatusCode.Created, CreatedUser)
            .Enqueue(HttpStatusCode.OK, """{"responses":[{"id":"g-1","status":204}]}""");

        await _sut.CreateAsync(Request(groups: [new TemplateGroup { Id = "g-1", DisplayName = "Sales-Team" }]), CancellationToken.None);

        var logged = _actions.Query(new ActionLogQuery { TargetId = "g-1" }).Single();
        logged.TargetType.Should().Be(ObjectType.Group);
        logged.Action.Should().Be("group.member.add");
    }
}
