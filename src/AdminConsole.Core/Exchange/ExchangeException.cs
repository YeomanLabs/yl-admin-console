using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// A failed cmdlet, or with <see cref="StatusCode"/> 0 a request that never reached Exchange. Carries what the UX §7
/// Details expander needs, plus the one thing in an Exchange error worth keying on: the .NET exception type.
/// </summary>
/// <remarks>
/// <c>error.message</c> is always some variant of "Error executing cmdlet". The content is in
/// <c>error.details[0].message</c> as <c>{trace id}|{exception type}|{text}</c> (doc 13 §1).
/// </remarks>
public sealed partial class ExchangeException : Exception
{
    public const string NetworkErrorCode = "NetworkError";

    public ExchangeException(
        int statusCode,
        string errorCode,
        string message,
        string? exceptionType = null,
        string? traceId = null,
        string? requestId = null,
        string? cmdlet = null,
        TimeSpan? retryAfter = null,
        string? rawBody = null,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ExceptionType = exceptionType;
        TraceId = traceId;
        RequestId = requestId;
        Cmdlet = cmdlet;
        RetryAfter = retryAfter;
        RawBody = rawBody;
        Timestamp = DateTimeOffset.UtcNow;
    }

    /// <summary>HTTP status, or 0 when the request never reached Exchange.</summary>
    public int StatusCode { get; }

    /// <summary><c>error.code</c>, e.g. <c>NotFound</c>, <c>BadRequest</c>; <see cref="NetworkErrorCode"/> for transport failures.</summary>
    public string ErrorCode { get; }

    /// <summary>Full .NET type of the server-side exception, e.g. <c>Microsoft.Exchange.Configuration.Tasks.ManagementObjectNotFoundException</c>.</summary>
    public string? ExceptionType { get; }

    /// <summary>The type's simple name, which is what the friendly-text map keys on.</summary>
    public string? ExceptionName => ExceptionType is { Length: > 0 } t ? t[(t.LastIndexOf('.') + 1)..] : null;

    /// <summary>Exchange's own trace prefix, e.g. <c>Ex6F9304</c>. Useful to Microsoft support, not to an admin.</summary>
    public string? TraceId { get; }

    public string? RequestId { get; }
    public string? Cmdlet { get; }
    public TimeSpan? RetryAfter { get; }
    public string? RawBody { get; }
    public DateTimeOffset Timestamp { get; }

    public bool IsThrottled => StatusCode == 429;
    public bool IsForbidden => StatusCode == 403;
    public bool IsUnauthorized => StatusCode == 401;
    public bool IsNetwork => StatusCode == 0 && ErrorCode == NetworkErrorCode;

    public bool IsNotFound =>
        StatusCode == 404 || string.Equals(ExceptionName, "ManagementObjectNotFoundException", StringComparison.Ordinal);

    public static ExchangeException FromResponse(int statusCode, string? body, HttpResponseHeaders? headers, string? cmdlet, DateTimeOffset now)
    {
        var (code, message, traceId, type, text) = ParseErrorBody(body);
        return new ExchangeException(
            statusCode,
            code ?? $"Http{statusCode}",
            text ?? message ?? $"Exchange Online returned HTTP {statusCode}.",
            type,
            traceId,
            HeaderValue(headers, "request-id"),
            cmdlet,
            RetryDelay(headers, now, TimeSpan.MaxValue),
            body);
    }

    /// <summary>
    /// How long Exchange asked us to wait: <c>Retry-After</c> as given, else — when the rate budget is spent — until
    /// <c>Rate-Limit-Reset</c>, capped at <paramref name="cap"/>. Null when the response says nothing either way.
    /// </summary>
    public static TimeSpan? RetryDelay(HttpResponseHeaders? headers, DateTimeOffset now, TimeSpan cap)
    {
        if (headers?.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            if (retryAfter.Date is { } date) return date > now ? date - now : TimeSpan.Zero;
        }

        if (HeaderValue(headers, "Rate-Limit-Remaining") is { } remaining &&
            int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out var left) && left <= 0 &&
            HeaderValue(headers, "Rate-Limit-Reset") is { } reset &&
            DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetAt))
        {
            var wait = resetAt > now ? resetAt - now : TimeSpan.Zero;
            return wait > cap ? cap : wait;
        }

        return null;
    }

    private static (string? Code, string? Message, string? TraceId, string? Type, string? Text) ParseErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return default;

            var code = ExchangeValues.NonEmpty(error, "code");
            var message = ExchangeValues.NonEmpty(error, "message");
            string? detail = null;
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                detail = details.EnumerateArray()
                    .Select(d => ExchangeValues.NonEmpty(d, "message"))
                    .FirstOrDefault(m => m is not null);
            }

            if (detail is null) return (code, message, null, null, null);

            var parts = detail.Split('|', 3);
            return parts.Length == 3
                ? (code, message, NullIfEmpty(parts[0]), NullIfEmpty(parts[1]), Clean(parts[2]))
                : (code, message, null, null, Clean(detail));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>"…couldn't be found on 'MW4PR19A17DC006.nampr19a017.prod.outlook.com'." names a server nobody can act on.</summary>
    private static string? Clean(string text)
    {
        var cleaned = ServerSuffix().Replace(text, string.Empty).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? HeaderValue(HttpResponseHeaders? headers, string name) =>
        headers is not null && headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    [GeneratedRegex(@"\s+on\s+'[A-Za-z0-9.\-]+\.(?:outlook\.com|outlook\.cn|office365\.us)'", RegexOptions.IgnoreCase)]
    private static partial Regex ServerSuffix();
}
