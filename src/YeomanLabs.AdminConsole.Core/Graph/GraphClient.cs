using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.Core.Graph;

/// <summary>
/// Raw Microsoft Graph over <see cref="HttpClient"/> (docs/03-ARCHITECTURE.md §GraphClient). No SDK.
/// Handles auth headers, 401 refresh, 429/5xx retry with Retry-After, paging, and per-inner-response batch retry.
/// </summary>
public sealed class GraphClient
{
    public const string V1BaseAddress = "https://graph.microsoft.com/v1.0/";
    public const string BetaBaseAddress = "https://graph.microsoft.com/beta/";
    private const int BatchChunkSize = 20;

    private static readonly Uri V1Base = new(V1BaseAddress);
    private static readonly Uri BetaBase = new(BetaBaseAddress);

    private readonly HttpClient _http;
    private readonly IAuthService _auth;
    private readonly ILogger<GraphClient> _logger;
    private readonly GraphClientOptions _options;
    private readonly SemaphoreSlim _gate;

    public GraphClient(HttpClient http, IAuthService auth, ILogger<GraphClient> logger, GraphClientOptions? options = null)
    {
        _http = http;
        _auth = auth;
        _logger = logger;
        _options = options ?? new GraphClientOptions();
        _gate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);

        _http.BaseAddress ??= V1Base;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        }
    }

    /// <summary>Sends to v1.0. <paramref name="relativeUrl"/> may also be an absolute URL (e.g. a nextLink).</summary>
    public Task<JsonDocument?> SendAsync(HttpMethod method, string relativeUrl, object? body, CancellationToken ct) =>
        SendCoreAsync(method, Resolve(relativeUrl, V1Base), body, ct);

    /// <summary>Sends to beta. Every call site must carry a comment explaining why v1.0 cannot do it (doc 03).</summary>
    public Task<JsonDocument?> SendBetaAsync(HttpMethod method, string relativeUrl, object? body, CancellationToken ct) =>
        SendCoreAsync(method, Resolve(relativeUrl, BetaBase), body, ct);

    /// <summary>GETs one page of a collection, cloning <c>value</c> items and capturing next/delta links.</summary>
    public async Task<GraphPage> GetPageAsync(string url, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false)
            ?? throw new GraphException(0, "EmptyResponse", "Graph returned an empty body for a collection request.");

        var root = doc.RootElement;
        var items = new List<JsonElement>();
        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                items.Add(item.Clone());
            }
        }

        return new GraphPage
        {
            Items = items,
            NextLink = GetString(root, "@odata.nextLink"),
            DeltaLink = GetString(root, "@odata.deltaLink"),
        };
    }

    /// <summary>Follows <c>@odata.nextLink</c> to the end, yielding each item.</summary>
    public async IAsyncEnumerable<JsonElement> GetAllPagesAsync(string url, [EnumeratorCancellation] CancellationToken ct)
    {
        string? next = url;
        while (next is not null)
        {
            var page = await GetPageAsync(next, ct).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                yield return item;
            }

            next = page.NextLink;
        }
    }

    /// <summary>
    /// Runs the requests through <c>POST /$batch</c> in chunks of 20. Inner 429/5xx responses are retried on their own
    /// (waiting the largest Retry-After among them) without resending the ones that succeeded. Result is keyed by id.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, CancellationToken ct)
    {
        var results = new Dictionary<string, BatchResponse>(requests.Count, StringComparer.Ordinal);
        if (requests.Count == 0) return results;

        foreach (var chunk in requests.Chunk(BatchChunkSize))
        {
            var byId = chunk.ToDictionary(r => r.Id, StringComparer.Ordinal);
            IReadOnlyList<BatchRequest> pending = chunk;

            for (var attempt = 1; ; attempt++)
            {
                var payload = new { requests = pending.Select(ToWire).ToArray() };
                using var doc = await SendAsync(HttpMethod.Post, "$batch", payload, ct).ConfigureAwait(false)
                    ?? throw new GraphException(0, "EmptyResponse", "Graph returned an empty body for $batch.");

                var retry = new List<BatchRequest>();
                TimeSpan? retryAfter = null;

                if (doc.RootElement.TryGetProperty("responses", out var responses) && responses.ValueKind == JsonValueKind.Array)
                {
                    foreach (var inner in responses.EnumerateArray())
                    {
                        var response = ParseInner(inner);
                        if (IsTransient(response.Status) && attempt < _options.MaxAttempts && byId.TryGetValue(response.Id, out var original))
                        {
                            retry.Add(original);
                            response.Headers.TryGetValue("Retry-After", out var ra);
                            var wait = GraphException.ParseRetryAfterSeconds(ra);
                            if (wait is not null && (retryAfter is null || wait > retryAfter)) retryAfter = wait;
                            continue;
                        }

                        results[response.Id] = response;
                    }
                }

                foreach (var missing in pending.Where(p => !results.ContainsKey(p.Id) && !retry.Contains(p)))
                {
                    // Graph omitted an inner response entirely; surface it rather than silently dropping the id.
                    results[missing.Id] = new BatchResponse
                    {
                        Id = missing.Id,
                        Status = 0,
                        Headers = new Dictionary<string, string>(),
                    };
                }

                if (retry.Count == 0) break;

                var delay = retryAfter ?? Backoff(attempt);
                _logger.LogWarning("Batch: {Count} inner request(s) throttled; retrying in {Delay}s (attempt {Attempt}/{Max})",
                    retry.Count, delay.TotalSeconds, attempt + 1, _options.MaxAttempts);
                await _options.Delay(delay, ct).ConfigureAwait(false);
                pending = retry;
            }
        }

        return results;
    }

    private async Task<JsonDocument?> SendCoreAsync(HttpMethod method, Uri uri, object? body, CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync(_options.TokenScopes, ct).ConfigureAwait(false);
        var refreshed = false;
        var jsonBody = body is null ? null : body as string ?? JsonSerializer.Serialize(body, GraphJson.Options);

        for (var attempt = 1; ; attempt++)
        {
            using var request = BuildRequest(method, uri, jsonBody, token);
            HttpResponseMessage response;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _logger.LogDebug("{Method} {Path} (attempt {Attempt})", method, uri.AbsolutePath, attempt);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxAttempts)
            {
                var delay = Backoff(attempt);
                _logger.LogWarning(ex, "{Method} {Path}: network error; retrying in {Delay}s", method, uri.AbsolutePath, delay.TotalSeconds);
                await _options.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new GraphException(0, GraphException.NetworkErrorCode, "Can't reach Microsoft Graph.", inner: ex);
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
                    _logger.LogInformation("{Method} {Path}: 401; refreshing token once", method, uri.AbsolutePath);
                    token = await _auth.GetTokenAsync(_options.TokenScopes, ct, forceRefresh: true).ConfigureAwait(false);
                    continue;
                }

                if (IsTransient(status) && attempt < _options.MaxAttempts)
                {
                    var delay = GraphException.ParseRetryAfter(response.Headers) ?? Backoff(attempt);
                    _logger.LogWarning("{Method} {Path}: HTTP {Status}; retrying in {Delay}s (attempt {Next}/{Max})",
                        method, uri.AbsolutePath, status, delay.TotalSeconds, attempt + 1, _options.MaxAttempts);
                    await _options.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                var ex = GraphException.FromResponse(status, text, response.Headers);
                _logger.LogWarning("{Method} {Path}: HTTP {Status} {Code} request-id={RequestId}", method, uri.AbsolutePath, status, ex.ErrorCode, ex.RequestId);
                throw ex;
            }
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, Uri uri, string? jsonBody, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("client-request-id", Guid.NewGuid().ToString());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Doc 04: ConsistencyLevel is required for $count (query option or the /$count segment) and $search.
        var pathAndQuery = uri.PathAndQuery;
        if (pathAndQuery.Contains("$count", StringComparison.OrdinalIgnoreCase) || pathAndQuery.Contains("$search", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = TimeSpan.FromTicks(_options.BaseDelay.Ticks * (1L << Math.Min(attempt - 1, 10)));
        if (exponential > _options.MaxDelay) exponential = _options.MaxDelay;
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        return exponential + jitter;
    }

    private static bool IsTransient(int status) => status == 429 || status >= 500;

    private static Uri Resolve(string url, Uri versionBase)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)) return absolute;
        return new Uri(versionBase, url.TrimStart('/'));
    }

    private static object ToWire(BatchRequest r)
    {
        var headers = r.Headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(r.Headers);
        if (r.Body is not null && !headers.ContainsKey("Content-Type"))
        {
            headers["Content-Type"] = "application/json";
        }

        return new
        {
            id = r.Id,
            method = r.Method.Method,
            url = r.Url.StartsWith('/') ? r.Url : "/" + r.Url,
            headers = headers.Count == 0 ? null : headers,
            body = r.Body,
        };
    }

    private static BatchResponse ParseInner(JsonElement inner)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inner.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in h.EnumerateObject())
            {
                headers[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText();
            }
        }

        JsonElement? body = null;
        if (inner.TryGetProperty("body", out var b) && b.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            body = b.Clone();
        }

        return new BatchResponse
        {
            Id = GetString(inner, "id") ?? string.Empty,
            Status = inner.TryGetProperty("status", out var s) && s.TryGetInt32(out var status) ? status : 0,
            Body = body,
            Headers = headers,
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
