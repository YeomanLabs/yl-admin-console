using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Tests.Fakes;
using AdminConsole.Core.Users;

namespace AdminConsole.Core.Tests.Users;

/// <summary>M18.1: issuing a Temporary Access Pass, shown once and never logged.</summary>
public sealed class TapTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Pass = "Xy7!kPq2Lm";
    private readonly FakeHttpHandler _http = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly UserService _sut;

    public TapTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var graph = new GraphClient(new HttpClient(_http), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new UserService(graph, _db, _actions, _tenant, NullLogger<UserService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    [Fact]
    public async Task Issue_PostsLifetimeAndOnce_ReturnsThePass_AndLogsEverythingButIt()
    {
        _http.Enqueue(HttpStatusCode.OK, """{"value":[]}""")
            .Enqueue(HttpStatusCode.Created, $$"""{"id":"tap-1","temporaryAccessPass":"{{Pass}}","startDateTime":"2026-10-17T09:00:00Z","lifetimeInMinutes":120,"isUsableOnce":true}""");

        var result = await _sut.IssueTemporaryAccessPassAsync("u-1", 120, usableOnce: true, start: null, Ct);

        result.Outcome.Success.Should().BeTrue();
        result.Pass.Should().Be(Pass);
        result.LifetimeMinutes.Should().Be(120);
        result.Caption.Should().Contain("2 hours").And.Contain("one sign-in");
        _http.Requests[1].Body.Should().Contain("\"lifetimeInMinutes\":120").And.Contain("\"isUsableOnce\":true").And.NotContain("startDateTime");

        var row = _actions.Query(new ActionLogQuery { ActionPrefix = "user.auth.tap" }).Single();
        (row.RequestSummary + row.Message).Should().NotContain(Pass, "the pass is shown once and goes nowhere else");
    }

    [Fact]
    public async Task Issue_ALaterStart_IsSentInUtc()
    {
        _http.Enqueue(HttpStatusCode.OK, """{"value":[]}""")
            .Enqueue(HttpStatusCode.Created, $$"""{"id":"tap-1","temporaryAccessPass":"{{Pass}}","lifetimeInMinutes":60,"isUsableOnce":false}""");

        await _sut.IssueTemporaryAccessPassAsync("u-1", 60, usableOnce: false, start: new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.FromHours(1)), Ct);

        _http.Requests[1].Body.Should().Contain("\"startDateTime\":\"2026-10-19T07:00:00Z\"");
    }

    [Fact]
    public async Task Issue_WhenOneAlreadyExists_SaysSoWithoutPosting()
    {
        _http.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"tap-old","lifetimeInMinutes":60}]}""");

        var result = await _sut.IssueTemporaryAccessPassAsync("u-1", 60, true, null, Ct);

        result.Outcome.Success.Should().BeFalse();
        result.Outcome.FriendlyError.Should().Contain("already has a Temporary Access Pass");
        _http.Requests.Should().ContainSingle("nothing is posted");
    }

    [Fact]
    public async Task Issue_ALifetimeOutsideThePolicy_PassesGraphsRangeThrough()
    {
        _http.Enqueue(HttpStatusCode.OK, """{"value":[]}""")
            .Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"badRequest","message":"The specified lifetime of 4320 minutes is outside the policy range of 60 to 480 minutes."}}""");

        var result = await _sut.IssueTemporaryAccessPassAsync("u-1", 4320, true, null, Ct);

        result.Outcome.Success.Should().BeFalse();
        result.Pass.Should().BeNull();
        result.Outcome.FriendlyError.Should().Contain("60 to 480");
    }
}
