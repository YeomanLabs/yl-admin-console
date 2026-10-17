using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Exchange;

public sealed class ExchangeClientTests
{
    private const string Endpoint = "https://outlook.office365.com/adminapi/beta/t-1/InvokeCommand";

    private readonly FakeHttpHandler _handler = new();
    private readonly FakeAuthService _auth = new();
    private readonly TenantContext _tenant = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeTimeProvider _time = new();

    public ExchangeClientTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
    }

    private ExchangeClient CreateClient(int maxAttempts = 5) => new(
        new HttpClient(_handler),
        _auth,
        _tenant,
        NullLogger<ExchangeClient>.Instance,
        new ExchangeClientOptions
        {
            MaxAttempts = maxAttempts,
            BaseDelay = TimeSpan.FromMilliseconds(10),
            Delay = (d, _) => { _delays.Add(d); return Task.CompletedTask; },
            Time = _time,
        });

    [Fact]
    public async Task Invoke_PostsTheCmdletEnvelope_WithTheRoutingHeaders()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[{"PrimarySmtpAddress":"adele@contoso.com"}]}""");

        var result = await CreateClient().InvokeAsync("Get-Mailbox",
            new Dictionary<string, object?> { ["Identity"] = "00000000-0000-0000-0000-000000000101" }, CancellationToken.None);

        result.Items.Should().ContainSingle();
        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsoluteUri.Should().Be(Endpoint);
        request.Headers["Authorization"].Should().Be("Bearer token-1");
        request.Headers["X-AnchorMailbox"].Should().Be("UPN:admin@contoso.com");
        request.Headers["X-ResponseFormat"].Should().Be("json");
        request.Headers["Prefer"].Should().Be("odata.maxpagesize=1000");
        request.Body.Should().Be("""{"CmdletInput":{"CmdletName":"Get-Mailbox","Parameters":{"Identity":"00000000-0000-0000-0000-000000000101"}}}""");
    }

    [Fact]
    public async Task Invoke_WithoutParameters_OmitsTheParametersObject_AndSendsSelect()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await CreateClient().InvokeAsync("Get-AcceptedDomain", null, CancellationToken.None, ["DomainName", "Default"]);

        var request = _handler.Requests.Single();
        request.Uri.AbsoluteUri.Should().Be(Endpoint + "?$select=DomainName,Default");
        request.Body.Should().Be("""{"CmdletInput":{"CmdletName":"Get-AcceptedDomain"}}""");
    }

    [Fact]
    public async Task Invoke_SerializesNullsAndMultiValues_TheWayTheEndpointExpects()
    {
        _handler.Enqueue(HttpStatusCode.OK);

        await CreateClient().InvokeAsync("Set-Mailbox", new Dictionary<string, object?>
        {
            ["Identity"] = "u-1",
            ["ForwardingAddress"] = null,
            ["HiddenFromAddressListsEnabled"] = true,
            ["EmailAddresses"] = ExchangeMultiValue.Adding("smtp:a@contoso.com"),
            ["GrantSendOnBehalfTo"] = new ExchangeMultiValue { Add = ["x"], Remove = ["y"] },
        }, CancellationToken.None);

        using var body = JsonDocument.Parse(_handler.Requests.Single().Body!);
        var p = body.RootElement.GetProperty("CmdletInput").GetProperty("Parameters");
        p.GetProperty("ForwardingAddress").ValueKind.Should().Be(JsonValueKind.Null);
        p.GetProperty("HiddenFromAddressListsEnabled").GetBoolean().Should().BeTrue();
        p.GetProperty("EmailAddresses").GetRawText()
            .Should().Be("""{"@odata.type":"#Exchange.GenericHashTable","add":["smtp:a@contoso.com"]}""");
        p.GetProperty("GrantSendOnBehalfTo").GetRawText()
            .Should().Be("""{"@odata.type":"#Exchange.GenericHashTable","add":["x"],"remove":["y"]}""");
    }

    [Fact]
    public async Task Invoke_FollowsNextLink_WithTheSameBody_AndConcatenatesRows()
    {
        var page1 = Fixtures.Text("exchange/mailboxes-page1");
        using (var doc = JsonDocument.Parse(page1))
        {
            doc.RootElement.TryGetProperty("@odata.nextLink", out _).Should().BeTrue("the fixture is a real first page");
        }

        _handler
            .Enqueue(HttpStatusCode.OK, page1)
            .Enqueue(HttpStatusCode.OK, """{"value":[{"UserPrincipalName":"last@contoso.com"}]}""");

        var result = await CreateClient().InvokeAsync("Get-Mailbox",
            new Dictionary<string, object?> { ["ResultSize"] = 3 }, CancellationToken.None, ["UserPrincipalName"]);

        result.Items.Should().HaveCount(4);
        _handler.Requests.Should().HaveCount(2);
        _handler.Requests[1].Body.Should().Be(_handler.Requests[0].Body);
        _handler.Requests[1].Uri.AbsoluteUri.Should().Contain("InvokeCommand").And.NotBe(_handler.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Invoke_CollectsWarnings_EvenWhenTheWriteSucceeded()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"@adminapi.warnings":["The command completed successfully but no settings of 'x' have been modified."],"value":[]}""");

        var result = await CreateClient().InvokeAsync("Set-Mailbox", new Dictionary<string, object?> { ["Identity"] = "u" }, CancellationToken.None);

        result.Warnings.Should().ContainSingle().Which.Should().Contain("no settings");
    }

    [Fact]
    public async Task Invoke_DropsTheMoreResultsWarning_OnPagesItFollows_ButKeepsOthers()
    {
        // Observed live: every page with a nextLink carries this warning, though the client goes on to fetch the rest.
        const string more = "There are more results available than are currently displayed. To view them, increase the value for the ResultSize parameter.";
        _handler
            .Enqueue(HttpStatusCode.OK, $$"""{"@adminapi.warnings":["{{more}}","Something else."],"value":[{"a":1}],"@odata.nextLink":"https://outlook.office365.com/adminapi/beta/t-1/InvokeCommand?$skiptoken=x"}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"@adminapi.warnings":["{{more}}"],"value":[{"a":2}]}""");

        var result = await CreateClient().InvokeAsync("Get-Mailbox", null, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Warnings.Should().Equal("Something else.", more);
    }

    [Fact]
    public async Task Invoke_EmptyBody_IsASuccessWithNoRows()
    {
        _handler.Enqueue(HttpStatusCode.OK);

        var result = await CreateClient().InvokeAsync("Set-Mailbox", new Dictionary<string, object?> { ["Identity"] = "u" }, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Invoke_Retries429_HonouringRetryAfter()
    {
        _handler
            .Enqueue(HttpStatusCode.TooManyRequests, null, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7)))
            .Enqueue(HttpStatusCode.OK, """{"value":[{"a":1}]}""");

        var result = await CreateClient().InvokeAsync("Get-Mailbox", null, CancellationToken.None);

        result.Items.Should().ContainSingle();
        _delays.Should().Equal(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Invoke_Retries429_WithoutRetryAfter_WaitsForTheRateLimitReset()
    {
        var reset = _time.GetUtcNow().AddSeconds(12);
        _handler
            .Enqueue(HttpStatusCode.TooManyRequests, null, r =>
            {
                r.Headers.TryAddWithoutValidation("Rate-Limit-Remaining", "0");
                r.Headers.TryAddWithoutValidation("Rate-Limit-Reset", reset.ToString("O"));
            })
            .Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await CreateClient().InvokeAsync("Get-Mailbox", null, CancellationToken.None);

        _delays.Should().Equal(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task Invoke_RefreshesTheTokenOnce_On401()
    {
        _handler
            .Enqueue(HttpStatusCode.Unauthorized)
            .Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await CreateClient().InvokeAsync("Get-Mailbox", null, CancellationToken.None);

        _auth.ForceRefreshes.Should().Be(1);
        _handler.Requests[1].Headers["Authorization"].Should().Be("Bearer token-2");
    }

    [Fact]
    public async Task Invoke_NotFound_CarriesTheExceptionType_AndDropsTheServerName()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, Fixtures.Text("exchange/error-not-found"), r => r.Headers.TryAddWithoutValidation("request-id", "req-9"));

        var act = () => CreateClient().InvokeAsync("Get-Recipient", new Dictionary<string, object?> { ["Identity"] = "nobody" }, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ExchangeException>()).Which;
        ex.StatusCode.Should().Be(404);
        ex.ErrorCode.Should().Be("NotFound");
        ex.IsNotFound.Should().BeTrue();
        ex.ExceptionName.Should().Be("ManagementObjectNotFoundException");
        ex.TraceId.Should().Be("Ex6F9304");
        ex.RequestId.Should().Be("req-9");
        ex.Cmdlet.Should().Be("Get-Recipient");
        ex.Message.Should().StartWith("The operation couldn't be performed because object")
            .And.NotContain("outlook.com")
            .And.NotContain("Error executing cmdlet");
        ex.RawBody.Should().Contain("prod.outlook.com", "the raw body keeps everything for the Details expander");
    }

    [Fact]
    public async Task Invoke_BadParameter_IsNotRetried_AndNamesTheParameterSetException()
    {
        _handler.Enqueue(HttpStatusCode.BadRequest, Fixtures.Text("exchange/error-bad-parameter"));

        var act = () => CreateClient().InvokeAsync("Get-Mailbox", new Dictionary<string, object?> { ["NoSuchParameter"] = "x" }, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ExchangeException>()).Which;
        ex.ExceptionName.Should().Be("AmbiguousParameterSetException");
        ex.TraceId.Should().BeNull("the 400 detail starts with an empty trace segment");
        _handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Invoke_GivesUpAfterMaxAttempts_OnServerErrors()
    {
        for (var i = 0; i < 3; i++) _handler.Enqueue(HttpStatusCode.ServiceUnavailable);

        var act = () => CreateClient(maxAttempts: 3).InvokeAsync("Get-Mailbox", null, CancellationToken.None);

        (await act.Should().ThrowAsync<ExchangeException>()).Which.StatusCode.Should().Be(503);
        _handler.Requests.Should().HaveCount(3);
        _delays.Should().HaveCount(2);
    }

    [Fact]
    public async Task Invoke_NetworkFailure_BecomesANetworkError()
    {
        for (var i = 0; i < 2; i++) _handler.Enqueue(_ => throw new HttpRequestException("no route"));

        var act = () => CreateClient(maxAttempts: 2).InvokeAsync("Get-Mailbox", null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ExchangeException>()).Which;
        ex.IsNetwork.Should().BeTrue();
        ExchangeErrorText.Friendly(ex).Should().StartWith("Can't reach Exchange Online");
    }

    [Fact]
    public async Task Invoke_BeforeTheTenantIsLoaded_Refuses()
    {
        var act = () => new ExchangeClient(new HttpClient(_handler), _auth, new TenantContext(), NullLogger<ExchangeClient>.Instance)
            .InvokeAsync("Get-Mailbox", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Invoke_AsksForTheExchangeAudience_NotGraph()
    {
        var scopes = new List<IReadOnlyList<string>>();
        var auth = new RecordingAuth(scopes);
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await new ExchangeClient(new HttpClient(_handler), auth, _tenant, NullLogger<ExchangeClient>.Instance)
            .InvokeAsync("Get-Mailbox", null, CancellationToken.None);

        scopes.Should().ContainSingle().Which.Should().Equal(Scopes.ExchangeManage);
    }

    private sealed class RecordingAuth(List<IReadOnlyList<string>> scopes) : IAuthService
    {
        public AuthAccount? CurrentAccount => null;
        public event EventHandler<AuthAccount?>? AccountChanged { add { } remove { } }
        public Task<AuthAccount> EnsureSignedInAsync(IReadOnlyList<string> s, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> EnsureScopesAsync(IReadOnlyList<string> s, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasScopeAsync(string scope, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetGrantedScopesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetTokenAsync(IReadOnlyList<string> s, CancellationToken ct, bool forceRefresh = false)
        {
            scopes.Add(s);
            return Task.FromResult("t");
        }
    }
}
