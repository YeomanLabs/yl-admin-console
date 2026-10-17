using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// Exchange Online's admin REST endpoint over <see cref="HttpClient"/> (docs/13-EXCHANGE-ONLINE.md §1). One call runs
/// one cmdlet through <c>adminapi/beta/{tenant}/InvokeCommand</c> as the signed-in admin and follows
/// <c>@odata.nextLink</c> to the end.
/// </summary>
/// <remarks>
/// Not <c>GraphClient</c> with a different URL: the request body, the error shape, the paging and the throttling signals
/// all differ, and sharing a class would make each of them a branch.
/// </remarks>
public sealed class ExchangeClient
{
    private const string MoreResultsWarning = "There are more results available";

    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        // Parameter names must reach Exchange exactly as written (Identity, ResultSize), so no naming policy.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly IAuthService _auth;
    private readonly TenantContext _tenant;
    private readonly ILogger<ExchangeClient> _logger;
    private readonly ExchangeClientOptions _options;
    private readonly SemaphoreSlim _gate;

    public ExchangeClient(HttpClient http, IAuthService auth, TenantContext tenant, ILogger<ExchangeClient> logger, ExchangeClientOptions? options = null)
    {
        _http = http;
        _auth = auth;
        _tenant = tenant;
        _logger = logger;
        _options = options ?? new ExchangeClientOptions();
        _gate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
    }

    /// <summary>
    /// Runs <paramref name="cmdlet"/> and returns every row across every page. Parameter values may be strings, numbers,
    /// booleans, string lists, <see cref="ExchangeMultiValue"/>, or null — a null is sent, because clearing a value is
    /// a deliberate act in Exchange just as in Graph.
    /// </summary>
    /// <param name="select">Properties to return. Without it <c>Get-Mailbox</c> answers with 338 of them.</param>
    /// <param name="anchorMailbox">
    /// The routing key for a call bound to one mailbox — <see cref="AnchorFor"/>. Omitted, the call is routed by the
    /// admin's own mailbox, which is right for tenant-wide cmdlets and wrong for anything that must run on the target
    /// mailbox's server: <c>Get-MailboxRegionalConfiguration</c> answers "Cmdlet needs proxy" (doc 13 §1).
    /// </param>
    public async Task<ExchangeResult> InvokeAsync(
        string cmdlet,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct,
        IReadOnlyList<string>? select = null,
        string? anchorMailbox = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cmdlet);
        if (string.IsNullOrEmpty(_tenant.TenantId) || string.IsNullOrEmpty(_tenant.SignedInUpn))
        {
            throw new InvalidOperationException("Exchange Online can't be called before the tenant is loaded.");
        }

        var body = BuildBody(cmdlet, parameters);
        string? url = BuildUrl(select);
        var items = new List<JsonElement>();
        var warnings = new List<string>();

        for (var page = 1; url is not null; page++)
        {
            if (page > _options.MaxPages)
            {
                throw new ExchangeException(0, "PagingLimit",
                    $"Exchange kept returning more pages for {cmdlet} after {_options.MaxPages}; stopped rather than loop forever.",
                    cmdlet: cmdlet);
            }

            using var doc = await SendAsync(url, body, cmdlet, anchorMailbox ?? $"UPN:{_tenant.SignedInUpn}", ct).ConfigureAwait(false);
            url = null;
            if (doc is null) break;

            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    items.Add(item.Clone());
                }
            }

            url = ExchangeValues.NonEmpty(root, "@odata.nextLink");

            // Every page that carries a nextLink also carries "There are more results available than are currently
            // displayed", which is PowerShell telling a person to raise ResultSize. This client follows the link, so the
            // warning is untrue by the time anyone could read it.
            warnings.AddRange(ExchangeValues.Strings(root, "@adminapi.warnings")
                .Where(w => url is null || !w.StartsWith(MoreResultsWarning, StringComparison.OrdinalIgnoreCase)));
        }

        if (warnings.Count > 0)
        {
            _logger.LogInformation("{Cmdlet}: {Count} warning(s) from Exchange", cmdlet, warnings.Count);
        }

        return new ExchangeResult { Items = items, Warnings = warnings };
    }

    /// <summary>
    /// The routing key that sends a call to the server holding <paramref name="userId"/>'s mailbox:
    /// <c>OID:{object id}@{tenant id}</c>, the form Microsoft documents for the Admin API and proven on this endpoint.
    /// </summary>
    public string AnchorFor(string userId) => $"OID:{userId}@{_tenant.TenantId}";

    private string BuildUrl(IReadOnlyList<string>? select)
    {
        var url = new Uri(_options.BaseAddress, $"adminapi/beta/{Uri.EscapeDataString(_tenant.TenantId)}/InvokeCommand").AbsoluteUri;
        return select is { Count: > 0 } ? $"{url}?$select={string.Join(',', select)}" : url;
    }

    private static string BuildBody(string cmdlet, IReadOnlyDictionary<string, object?>? parameters)
    {
        var input = new Dictionary<string, object?> { ["CmdletName"] = cmdlet };
        if (parameters is { Count: > 0 }) input["Parameters"] = parameters;
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["CmdletInput"] = input }, BodyOptions);
    }

    private async Task<JsonDocument?> SendAsync(string url, string body, string cmdlet, string anchor, CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync(_options.TokenScopes, ct).ConfigureAwait(false);
        var refreshed = false;

        for (var attempt = 1; ; attempt++)
        {
            using var request = BuildRequest(url, body, token, anchor);
            HttpResponseMessage response;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _logger.LogDebug("{Cmdlet} (attempt {Attempt})", cmdlet, attempt);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxAttempts)
            {
                var delay = Backoff(attempt);
                _logger.LogWarning(ex, "{Cmdlet}: network error; retrying in {Delay}s", cmdlet, delay.TotalSeconds);
                await _options.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new ExchangeException(0, ExchangeException.NetworkErrorCode, "Can't reach Exchange Online.", cmdlet: cmdlet, inner: ex);
            }
            finally
            {
                _gate.Release();
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var text = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
                }

                if (status == (int)HttpStatusCode.Unauthorized && !refreshed)
                {
                    refreshed = true;
                    _logger.LogInformation("{Cmdlet}: 401; refreshing the Exchange token once", cmdlet);
                    token = await _auth.GetTokenAsync(_options.TokenScopes, ct, forceRefresh: true).ConfigureAwait(false);
                    continue;
                }

                if (IsTransient(status) && attempt < _options.MaxAttempts)
                {
                    var delay = ExchangeException.RetryDelay(response.Headers, _options.Time.GetUtcNow(), _options.MaxDelay) ?? Backoff(attempt);
                    _logger.LogWarning("{Cmdlet}: HTTP {Status}; retrying in {Delay}s (attempt {Next}/{Max})",
                        cmdlet, status, delay.TotalSeconds, attempt + 1, _options.MaxAttempts);
                    await _options.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                var ex = ExchangeException.FromResponse(status, text, response.Headers, cmdlet, _options.Time.GetUtcNow());
                _logger.LogWarning("{Cmdlet}: HTTP {Status} {Code} {Type} request-id={RequestId}",
                    cmdlet, status, ex.ErrorCode, ex.ExceptionName, ex.RequestId);
                throw ex;
            }
        }
    }

    private HttpRequestMessage BuildRequest(string url, string body, string token, string anchor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        request.Headers.TryAddWithoutValidation("X-AnchorMailbox", anchor);
        request.Headers.TryAddWithoutValidation("X-ResponseFormat", "json");
        request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={_options.PageSize}");
        request.Headers.TryAddWithoutValidation("client-request-id", Guid.NewGuid().ToString());
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = TimeSpan.FromTicks(_options.BaseDelay.Ticks * (1L << Math.Min(attempt - 1, 10)));
        if (exponential > _options.MaxDelay) exponential = _options.MaxDelay;
        return exponential + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
    }

    private static bool IsTransient(int status) => status == 429 || status >= 500;
}
