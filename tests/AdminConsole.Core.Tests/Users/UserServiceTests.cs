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

public sealed class UserServiceTests : IDisposable
{
    private const string Id = "u-1";
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly UserService _sut;

    public UserServiceTests()
    {
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new UserService(graph, _db, _actions, _tenant, NullLogger<UserService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    [Fact]
    public async Task Hydrate_OneBatch_FillsEverySection_AndWritesThroughToCache()
    {
        _handler.Enqueue(HttpStatusCode.OK, Batch(
            ("user", 200, """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"assignedLicenses":[{"skuId":"s1"}],"signInActivity":{"lastSignInDateTime":"2026-09-08T10:00:00Z","lastNonInteractiveSignInDateTime":"2026-09-09T01:00:00Z"}}"""),
            ("manager", 200, """{"id":"m-1","displayName":"Grace Hopper","userPrincipalName":"grace@contoso.com"}"""),
            ("memberOf", 200, """{"value":[{"@odata.type":"#microsoft.graph.group","id":"g-1","displayName":"Sales","groupTypes":["Unified"],"mailEnabled":true,"securityEnabled":false},{"@odata.type":"#microsoft.graph.directoryRole","id":"r-1","displayName":null,"roleTemplateId":"fe930be7-5e62-47db-91af-98c3a49a38b1"},{"@odata.type":"#microsoft.graph.group","id":"g-2","displayName":"VPN","groupTypes":[],"mailEnabled":false,"securityEnabled":true,"onPremisesSyncEnabled":true}]}"""),
            ("auth", 200, """{"value":[{"@odata.type":"#microsoft.graph.passwordAuthenticationMethod","id":"p"},{"@odata.type":"#microsoft.graph.phoneAuthenticationMethod","id":"ph","phoneNumber":"+1 4255550100","phoneType":"mobile"}]}"""),
            ("devices", 200, """{"value":[{"id":"e-1","deviceId":"aad-1","displayName":"DESKTOP-1","trustType":"AzureAd","isCompliant":true}]}"""),
            ("licenses", 200, """{"id":"u-1","licenseAssignmentStates":[{"skuId":"s1","assignedByGroup":null,"state":"Active","error":"None","disabledPlans":[]},{"skuId":"s2","assignedByGroup":"g-1","state":"ActiveWithError","error":"CountViolation","disabledPlans":["p1"]}]}"""),
            ("reports", 200, """{"value":[{"id":"d-1","displayName":"Linus","userPrincipalName":"linus@contoso.com"},{"id":"d-2","displayName":"Margaret","userPrincipalName":"m@contoso.com"}]}""")));

        var d = await _sut.HydrateAsync(Id, CancellationToken.None);

        _handler.Requests.Should().ContainSingle("everything rides one $batch");
        using var sent = JsonDocument.Parse(_handler.Requests[0].Body!);
        var urls = sent.RootElement.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("url").GetString()!).ToList();
        urls.Should().HaveCount(7);
        urls[0].Should().StartWith("/users/u-1?$select=id,displayName").And.Contain("signInActivity");
        urls[1].Should().Be("/users/u-1/manager?$select=id,displayName,userPrincipalName");
        urls[2].Should().StartWith("/users/u-1/memberOf?$select=id,displayName,groupTypes").And.Contain("roleTemplateId").And.Contain("$top=999");
        urls[3].Should().Be("/users/u-1/authentication/methods");
        urls[4].Should().StartWith("/users/u-1/registeredDevices?$select=id,deviceId");
        urls[5].Should().Be("/users/u-1?$select=id,licenseAssignmentStates");
        urls[6].Should().StartWith("/users/u-1/directReports?$select=id,displayName,userPrincipalName");

        d.User.IsSuccess.Should().BeTrue();
        d.User.Value!.DisplayName.Should().Be("Ada Lovelace");
        d.SignInActivityUnavailable.Should().BeFalse();
        d.SignInActivity!.LastSeen.Should().Be(new DateTimeOffset(2026, 9, 9, 1, 0, 0, TimeSpan.Zero));
        d.Manager.Value!.Label.Should().Be("Grace Hopper");
        d.Groups.Value.Should().HaveCount(2);
        d.Groups.Value![0].Kind.Should().Be(GroupKind.M365);
        d.Groups.Value[1].IsSynced.Should().BeTrue();
        d.Roles.Value.Should().ContainSingle().Which.Should().BeEquivalentTo(new DirectoryRoleRef("User Administrator", DirectoryRoleRef.UserAdministrator));
        d.AuthMethods.Value.Should().HaveCount(2);
        d.AuthMethods.Value![1].Kind.Should().Be(AuthMethodKind.Phone);
        d.RegisteredDevices.Value.Should().ContainSingle().Which.DeviceId.Should().Be("aad-1");
        d.LicenseStates.Value.Should().HaveCount(2);
        d.LicenseStates.Value![1].IsGroupAssigned.Should().BeTrue();
        d.LicenseStates.Value[1].HasError.Should().BeTrue();
        d.LicenseStates.Value[0].HasError.Should().BeFalse("\"None\" is not an error");
        d.DirectReports.Value.Should().HaveCount(2);
        d.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(0);

        _db.GetUser(Id)!.DisplayName.Should().Be("Ada Lovelace", "write-through");
        _sut.GetCached(Id).Should().NotBeNull();
    }

    [Fact]
    public async Task Hydrate_NoManager404_IsOkNull_AndOtherSectionsFailIndependently()
    {
        _handler.Enqueue(HttpStatusCode.OK, Batch(
            ("user", 200, """{"id":"u-1","userPrincipalName":"a@b","accountEnabled":true}"""),
            ("manager", 404, """{"error":{"code":"Request_ResourceNotFound","message":"no manager"}}"""),
            ("memberOf", 200, """{"value":[]}"""),
            ("auth", 403, """{"error":{"code":"Authorization_RequestDenied","message":"needs UserAuthenticationMethod.Read.All"}}"""),
            ("devices", 200, """{"value":[]}"""),
            ("licenses", 200, """{"id":"u-1","licenseAssignmentStates":[]}"""),
            ("reports", 400, """{"error":{"code":"Request_BadRequest","message":"boom"}}""")));

        var d = await _sut.HydrateAsync(Id, CancellationToken.None);

        d.Manager.IsSuccess.Should().BeTrue();
        d.Manager.Value.Should().BeNull();
        d.AuthMethods.IsSuccess.Should().BeFalse();
        d.AuthMethods.IsForbidden.Should().BeTrue();
        d.AuthMethods.Error!.ErrorCode.Should().Be("Authorization_RequestDenied");
        d.DirectReports.Error!.StatusCode.Should().Be(400, "5xx would be retried by the client; 4xx is final");
        d.Groups.Value.Should().BeEmpty();
        d.Roles.Value.Should().BeEmpty();
        d.SignInActivity.Should().BeNull();
    }

    [Fact]
    public async Task Hydrate_UserRead403_RetriesWithoutSignInActivity_AndFlagsIt()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, Batch(
                ("user", 403, """{"error":{"code":"Authorization_RequestDenied","message":"signInActivity needs AuditLog.Read.All"}}"""),
                ("manager", 404, "{}"),
                ("memberOf", 200, """{"value":[]}"""),
                ("auth", 200, """{"value":[]}"""),
                ("devices", 200, """{"value":[]}"""),
                ("licenses", 200, """{"licenseAssignmentStates":[]}"""),
                ("reports", 200, """{"value":[]}""")))
            .Enqueue(HttpStatusCode.OK, """{"id":"u-1","displayName":"Ada","userPrincipalName":"ada@contoso.com","accountEnabled":true}""");

        var d = await _sut.HydrateAsync(Id, CancellationToken.None);

        _handler.Requests.Should().HaveCount(2);
        _handler.Requests[1].Uri.PathAndQuery.Should().StartWith("/v1.0/users/u-1?$select=").And.NotContain("signInActivity");
        d.User.IsSuccess.Should().BeTrue();
        d.User.Value!.DisplayName.Should().Be("Ada");
        d.SignInActivityUnavailable.Should().BeTrue();
        d.SignInActivity.Should().BeNull();
    }

    [Fact]
    public async Task Hydrate_UserGone_ReportsFailureWithoutThrowing()
    {
        _handler.Enqueue(HttpStatusCode.OK, Batch(
            ("user", 404, """{"error":{"code":"Request_ResourceNotFound","message":"gone"}}"""),
            ("manager", 404, "{}"), ("memberOf", 404, "{}"), ("auth", 404, "{}"), ("devices", 404, "{}"), ("licenses", 404, "{}"), ("reports", 404, "{}")));

        var d = await _sut.HydrateAsync(Id, CancellationToken.None);

        d.User.IsSuccess.Should().BeFalse();
        d.User.Error!.IsNotFound.Should().BeTrue();
        d.SignInActivityUnavailable.Should().BeFalse();
        _db.GetUser(Id).Should().BeNull("nothing was written through");
    }

    private static string Batch(params (string Id, int Status, string Body)[] inner) =>
        "{\"responses\":[" + string.Join(",", inner.Select(i => $"{{\"id\":\"{i.Id}\",\"status\":{i.Status},\"body\":{i.Body}}}")) + "]}";
}
