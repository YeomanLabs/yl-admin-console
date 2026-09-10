using System.Net.Http.Headers;
using System.Text.Json;

namespace AdminConsole.Core.Graph;

/// <summary>
/// A non-success Microsoft Graph response (or, with <see cref="StatusCode"/> 0, a network failure).
/// Carries everything the UX §7 "Details" expander needs.
/// </summary>
public sealed class GraphException : Exception
{
    public const string NetworkErrorCode = "NetworkError";

    /// <summary>Stand-in code for the Intune service endpoints, whose error bodies carry no code at all.</summary>
    public const string IntuneErrorCode = "IntuneServiceError";

    public GraphException(
        int statusCode,
        string errorCode,
        string message,
        string? requestId = null,
        string? clientRequestId = null,
        TimeSpan? retryAfter = null,
        string? rawBody = null,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        RequestId = requestId;
        ClientRequestId = clientRequestId;
        RetryAfter = retryAfter;
        RawBody = rawBody;
        Timestamp = DateTimeOffset.UtcNow;
    }

    /// <summary>HTTP status, or 0 when the request never reached Graph.</summary>
    public int StatusCode { get; }

    /// <summary>Graph <c>error.code</c>, e.g. <c>Request_ResourceNotFound</c>; <see cref="NetworkErrorCode"/> for transport failures.</summary>
    public string ErrorCode { get; }

    public string? RequestId { get; }
    public string? ClientRequestId { get; }
    public TimeSpan? RetryAfter { get; }
    public string? RawBody { get; }
    public DateTimeOffset Timestamp { get; }

    public bool IsThrottled => StatusCode == 429;
    public bool IsNotFound => StatusCode == 404;
    public bool IsForbidden => StatusCode == 403;

    /// <summary>Builds an exception from a Graph error payload, falling back to the status text when the body is not Graph-shaped.</summary>
    public static GraphException FromResponse(int statusCode, string? body, HttpResponseHeaders? headers)
    {
        var (code, message, innerRequestId, innerClientRequestId) = ParseErrorBody(body);
        var requestId = HeaderValue(headers, "request-id") ?? innerRequestId;
        var clientRequestId = HeaderValue(headers, "client-request-id") ?? innerClientRequestId;
        return new GraphException(
            statusCode,
            code ?? $"Http{statusCode}",
            message ?? $"Microsoft Graph returned HTTP {statusCode}.",
            requestId,
            clientRequestId,
            ParseRetryAfter(headers),
            body);
    }

    /// <summary>Same as <see cref="FromResponse(int, string?, HttpResponseHeaders?)"/> but for an inner <c>$batch</c> response.</summary>
    public static GraphException FromBatchResponse(int statusCode, JsonElement? body, IReadOnlyDictionary<string, string> headers)
    {
        var raw = body?.GetRawText();
        var (code, message, innerRequestId, innerClientRequestId) = ParseErrorBody(raw);
        headers.TryGetValue("request-id", out var requestId);
        headers.TryGetValue("client-request-id", out var clientRequestId);
        headers.TryGetValue("Retry-After", out var retryAfterText);
        return new GraphException(
            statusCode,
            code ?? $"Http{statusCode}",
            message ?? $"Microsoft Graph returned HTTP {statusCode}.",
            requestId ?? innerRequestId,
            clientRequestId ?? innerClientRequestId,
            ParseRetryAfterSeconds(retryAfterText),
            raw);
    }

    public static TimeSpan? ParseRetryAfter(HttpResponseHeaders? headers)
    {
        var retryAfter = headers?.RetryAfter;
        if (retryAfter is null) return null;
        if (retryAfter.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    public static TimeSpan? ParseRetryAfterSeconds(string? text) =>
        int.TryParse(text, out var seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;

    private static string? HeaderValue(HttpResponseHeaders? headers, string name) =>
        headers is not null && headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static (string? Code, string? Message, string? RequestId, string? ClientRequestId) ParseErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                // The Intune service endpoints behind deviceManagement answer with their own shape:
                // { "_version": 3, "Message": "...", "CustomApiErrorPhrase": "", ... }. Without this the whole JSON
                // blob ends up as the message an admin reads.
                var intune = GetString(doc.RootElement, "Message");
                return intune is null ? (null, null, null, null) : (IntuneErrorCode, intune, null, null);
            }

            string? requestId = null, clientRequestId = null;
            if (error.TryGetProperty("innerError", out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                requestId = GetString(inner, "request-id");
                clientRequestId = GetString(inner, "client-request-id");
            }

            return (GetString(error, "code"), GetString(error, "message"), requestId, clientRequestId);
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
