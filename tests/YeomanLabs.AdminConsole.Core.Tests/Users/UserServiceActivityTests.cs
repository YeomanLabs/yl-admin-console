using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.Core.Tests.Users;

public sealed class UserServiceActivityTests : IDisposable
{
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly UserService _sut;

    public UserServiceActivityTests()
    {
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new UserService(graph, _db, NullLogger<UserService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AuditEvents_QueryShapeAndParsing()
    {
        _handler.Enqueue(HttpStatusCode.OK, """
            {"value":[
              {"id":"a1","activityDisplayName":"Update user","activityDateTime":"2026-09-09T01:02:03Z","result":"success","category":"UserManagement",
               "initiatedBy":{"user":{"userPrincipalName":"admin@contoso.com","displayName":"Admin"},"app":null}},
              {"id":"a2","activityDisplayName":"Reset user password","activityDateTime":"2026-09-08T01:02:03Z","result":"failure","resultReason":"Insufficient privileges",
               "initiatedBy":{"user":null,"app":{"displayName":"Microsoft Approval Management"}}}
            ]}
            """);

        var r = await _sut.GetAuditEventsAsync("u-1", CancellationToken.None);

        r.IsSuccess.Should().BeTrue();
        r.Value.Should().HaveCount(2);
        r.Value![0].InitiatedBy.Should().Be("admin@contoso.com");
        r.Value[1].InitiatedBy.Should().Be("Microsoft Approval Management");
        r.Value[1].IsFailure.Should().BeTrue();
        r.Value[1].ResultReason.Should().Be("Insufficient privileges");
        var q = Uri.UnescapeDataString(_handler.Requests[0].Uri.PathAndQuery);
        q.Should().StartWith("/v1.0/auditLogs/directoryAudits?$filter=targetResources/any(t:t/id eq 'u-1')").And.Contain("$top=20").And.Contain("$orderby=activityDateTime desc");
    }

    [Fact]
    public async Task AuditEvents_403_IsAFailedSectionNotAnException()
    {
        _handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"needs AuditLog.Read.All"}}""");

        var r = await _sut.GetAuditEventsAsync("u-1", CancellationToken.None);

        r.IsSuccess.Should().BeFalse();
        r.IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task DeviceStates_OneBatch_SkipsMissingDevices()
    {
        _handler.Enqueue(HttpStatusCode.OK, """
            {"responses":[
              {"id":"m1","status":200,"body":{"id":"m1","complianceState":"noncompliant","isEncrypted":false,"lastSyncDateTime":"2026-09-09T06:00:00Z"}},
              {"id":"m2","status":404,"body":{"error":{"code":"ResourceNotFound","message":"retired"}}}
            ]}
            """);

        var states = await _sut.GetDeviceStatesAsync(["m1", "m2"], CancellationToken.None);

        states.Should().ContainKey("m1").WhoseValue.Should().BeEquivalentTo(new { ComplianceState = "noncompliant", IsEncrypted = false });
        states.Should().NotContainKey("m2");
        _handler.Requests.Should().ContainSingle().Which.Body.Should().Contain("/deviceManagement/managedDevices/m1?$select=id,complianceState,isEncrypted,lastSyncDateTime");
        (await _sut.GetDeviceStatesAsync([], CancellationToken.None)).Should().BeEmpty("no request for no devices");
        _handler.Requests.Should().HaveCount(1);
    }
}
