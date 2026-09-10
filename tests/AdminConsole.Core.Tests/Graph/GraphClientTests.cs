using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Graph;

public sealed class GraphClientTests
{
    private readonly FakeHttpHandler _handler = new();
    private readonly FakeAuthService _auth = new();
    private readonly List<TimeSpan> _delays = [];

    private GraphClient CreateClient(int maxAttempts = 5)
    {
        var options = new GraphClientOptions
        {
            MaxAttempts = maxAttempts,
            BaseDelay = TimeSpan.FromMilliseconds(10),
            Delay = (d, _) => { _delays.Add(d); return Task.CompletedTask; },
        };
        return new GraphClient(new HttpClient(_handler), _auth, NullLogger<GraphClient>.Instance, options);
    }

    [Fact]
    public async Task Send_Retries429HonoringRetryAfter_ThenReturnsBody()
    {
        _handler
            .Enqueue(HttpStatusCode.TooManyRequests, """{"error":{"code":"TooManyRequests","message":"slow down"}}""",
                r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3)))
            .Enqueue(HttpStatusCode.OK, """{"id":"u1","displayName":"Ada"}""");

        using var doc = await CreateClient().SendAsync(HttpMethod.Get, "users/u1", null, CancellationToken.None);

        doc!.RootElement.GetProperty("displayName").GetString().Should().Be("Ada");
        _handler.Requests.Should().HaveCount(2);
        _delays.Should().Equal(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Send_WithoutRetryAfter_UsesExponentialBackoff_AndGivesUpAfterMaxAttempts()
    {
        for (var i = 0; i < 5; i++)
        {
            _handler.Enqueue(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"ServiceUnavailable","message":"down"}}""");
        }

        var act = () => CreateClient(maxAttempts: 5).SendAsync(HttpMethod.Get, "users", null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<GraphException>()).Which;
        ex.StatusCode.Should().Be(503);
        ex.ErrorCode.Should().Be("ServiceUnavailable");
        _handler.Requests.Should().HaveCount(5);
        _delays.Should().HaveCount(4);
        _delays[1].Should().BeGreaterThan(_delays[0] - TimeSpan.FromMilliseconds(250));
        _delays[3].Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(80));
    }

    [Fact]
    public async Task GetAllPages_FollowsThreeNextLinks()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"1"},{"id":"2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=a"}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"3"},{"id":"4"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=b"}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"5"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=z"}""");

        var ids = new List<string>();
        await foreach (var item in CreateClient().GetAllPagesAsync("users/delta?$select=id", CancellationToken.None))
        {
            ids.Add(item.GetProperty("id").GetString()!);
        }

        ids.Should().Equal("1", "2", "3", "4", "5");
        _handler.Requests.Should().HaveCount(3);
        _handler.Requests[0].Uri.Should().Be(new Uri("https://graph.microsoft.com/v1.0/users/delta?$select=id"));
        _handler.Requests[1].Uri.Query.Should().Contain("skiptoken=a");
        _handler.Requests[2].Uri.Query.Should().Contain("skiptoken=b");
    }

    [Fact]
    public async Task GetPage_ExposesDeltaLinkAndClonesItemsBeyondDocumentLifetime()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"1","displayName":"x"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=z"}""");

        var page = await CreateClient().GetPageAsync("users/delta", CancellationToken.None);

        page.NextLink.Should().BeNull();
        page.DeltaLink.Should().EndWith("$deltatoken=z");
        page.Items.Should().ContainSingle().Which.GetProperty("displayName").GetString().Should().Be("x");
    }

    [Fact]
    public async Task Batch_RetriesOnlyTheThrottledInnerRequest()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, """
                {"responses":[
                  {"id":"1","status":200,"body":{"id":"m1"}},
                  {"id":"2","status":429,"headers":{"Retry-After":"2"},"body":{"error":{"code":"TooManyRequests","message":"x"}}},
                  {"id":"3","status":404,"body":{"error":{"code":"Request_ResourceNotFound","message":"no manager"}}}
                ]}
                """)
            .Enqueue(HttpStatusCode.OK, """{"responses":[{"id":"2","status":200,"body":{"value":[]}}]}""");

        var requests = new[]
        {
            BatchRequest.Get("1", "/users/a/manager"),
            BatchRequest.Get("2", "/users/a/memberOf"),
            BatchRequest.Get("3", "/users/b/manager"),
        };

        var results = await CreateClient().BatchAsync(requests, CancellationToken.None);

        results.Should().HaveCount(3);
        results["1"].IsSuccess.Should().BeTrue();
        results["2"].IsSuccess.Should().BeTrue();
        results["3"].Status.Should().Be(404);
        _delays.Should().Equal(TimeSpan.FromSeconds(2));

        _handler.Requests.Should().HaveCount(2);
        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/$batch");
        using var second = JsonDocument.Parse(_handler.Requests[1].Body!);
        var inner = second.RootElement.GetProperty("requests");
        inner.GetArrayLength().Should().Be(1);
        inner[0].GetProperty("id").GetString().Should().Be("2");
        inner[0].GetProperty("url").GetString().Should().Be("/users/a/memberOf");

        var notFound = () => results["3"].EnsureSuccess();
        notFound.Should().Throw<GraphException>().Which.ErrorCode.Should().Be("Request_ResourceNotFound");
    }

    [Fact]
    public async Task Batch_ChunksIntoTwenty()
    {
        _handler
            .Enqueue(r => FakeHttpHandler.Json(HttpStatusCode.OK, BuildBatchOk(r, 20)))
            .Enqueue(r => FakeHttpHandler.Json(HttpStatusCode.OK, BuildBatchOk(r, 5)));

        var requests = Enumerable.Range(1, 25).Select(i => BatchRequest.Get(i.ToString(), $"/users/{i}")).ToArray();
        var results = await CreateClient().BatchAsync(requests, CancellationToken.None);

        results.Should().HaveCount(25);
        _handler.Requests.Should().HaveCount(2);

        static string BuildBatchOk(HttpRequestMessage request, int expected)
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var ids = doc.RootElement.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
            ids.Should().HaveCount(expected);
            return "{\"responses\":[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"status\":200,\"body\":{{}}}}")) + "]}";
        }
    }

    [Fact]
    public async Task Send_4xx_SurfacesCodeMessageAndRequestId()
    {
        _handler.Enqueue(HttpStatusCode.NotFound,
            """{"error":{"code":"Request_ResourceNotFound","message":"Resource 'abc' does not exist.","innerError":{"request-id":"req-123","client-request-id":"cli-456","date":"2026-09-09T00:00:00"}}}""");

        var act = () => CreateClient().SendAsync(HttpMethod.Get, "users/abc", null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<GraphException>()).Which;
        ex.StatusCode.Should().Be(404);
        ex.ErrorCode.Should().Be("Request_ResourceNotFound");
        ex.Message.Should().Be("Resource 'abc' does not exist.");
        ex.RequestId.Should().Be("req-123");
        ex.ClientRequestId.Should().Be("cli-456");
        ex.IsNotFound.Should().BeTrue();
        ex.RawBody.Should().Contain("Request_ResourceNotFound");
        _handler.Requests.Should().HaveCount(1);
        _delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_401_RefreshesTokenOnceAndRetries()
    {
        _handler
            .Enqueue(HttpStatusCode.Unauthorized, """{"error":{"code":"InvalidAuthenticationToken","message":"expired"}}""")
            .Enqueue(HttpStatusCode.OK, """{"id":"u1"}""");

        using var doc = await CreateClient().SendAsync(HttpMethod.Get, "me", null, CancellationToken.None);

        doc.Should().NotBeNull();
        _auth.ForceRefreshes.Should().Be(1);
        _handler.Requests[0].Headers["Authorization"].Should().Be("Bearer token-1");
        _handler.Requests[1].Headers["Authorization"].Should().Be("Bearer token-2");
    }

    [Fact]
    public async Task Send_401Twice_Throws()
    {
        _handler
            .Enqueue(HttpStatusCode.Unauthorized, """{"error":{"code":"InvalidAuthenticationToken","message":"expired"}}""")
            .Enqueue(HttpStatusCode.Unauthorized, """{"error":{"code":"InvalidAuthenticationToken","message":"still expired"}}""");

        var act = () => CreateClient().SendAsync(HttpMethod.Get, "me", null, CancellationToken.None);

        (await act.Should().ThrowAsync<GraphException>()).Which.StatusCode.Should().Be(401);
        _auth.ForceRefreshes.Should().Be(1);
    }

    [Fact]
    public async Task Send_AddsConsistencyLevelOnlyForCountOrSearch()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, """{"value":[]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        var client = CreateClient();
        using var _ = await client.SendAsync(HttpMethod.Get, "users?$count=true&$filter=endsWith(mail,'x')", null, CancellationToken.None);
        using var __ = await client.SendAsync(HttpMethod.Get, "users?$select=id", null, CancellationToken.None);

        _handler.Requests[0].Headers.Should().ContainKey("ConsistencyLevel").WhoseValue.Should().Be("eventual");
        _handler.Requests[1].Headers.Should().NotContainKey("ConsistencyLevel");
        _handler.Requests[0].Headers.Should().ContainKey("client-request-id");
        _handler.Requests[0].Headers["User-Agent"].Should().StartWith("AdminConsole/");
        _handler.Requests[0].Headers["Accept"].Should().Be("application/json");
    }

    [Fact]
    public async Task Send_CountSegment_AcceptsTextPlainAndParsesTheNumber()
    {
        _handler.Enqueue(HttpStatusCode.OK, "4812");

        using var doc = await CreateClient().SendAsync(HttpMethod.Get, "users/$count", null, CancellationToken.None);

        doc!.RootElement.GetInt32().Should().Be(4812);
        _handler.Requests[0].Headers["Accept"].Should().Be("text/plain", "Graph answers /$count as text/plain and 415s on application/json");
        _handler.Requests[0].Headers.Should().ContainKey("ConsistencyLevel");
    }

    [Fact]
    public async Task Send_ResolvesRelativeAndBetaUrls_AndSerializesBodyCamelCase()
    {
        _handler
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.OK, """{"ok":true}""");

        var client = CreateClient();
        using var patch = await client.SendAsync(HttpMethod.Patch, "/users/u1", new { AccountEnabled = false, Department = (string?)null }, CancellationToken.None);
        using var beta = await client.SendBetaAsync(HttpMethod.Get, "directory/deviceLocalCredentials/d1", null, CancellationToken.None);

        patch.Should().BeNull();
        _handler.Requests[0].Uri.Should().Be(new Uri("https://graph.microsoft.com/v1.0/users/u1"));
        _handler.Requests[0].Body.Should().Be("""{"accountEnabled":false,"department":null}""");
        _handler.Requests[1].Uri.Should().Be(new Uri("https://graph.microsoft.com/beta/directory/deviceLocalCredentials/d1"));
    }

    [Fact]
    public async Task Send_NetworkFailure_RetriesThenThrowsNetworkError()
    {
        _handler
            .Enqueue(_ => throw new HttpRequestException("dns"))
            .Enqueue(_ => throw new HttpRequestException("dns"));

        var act = () => CreateClient(maxAttempts: 2).SendAsync(HttpMethod.Get, "me", null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<GraphException>()).Which;
        ex.StatusCode.Should().Be(0);
        ex.ErrorCode.Should().Be(GraphException.NetworkErrorCode);
        _delays.Should().HaveCount(1);
    }
}
