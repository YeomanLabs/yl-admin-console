using System.Net.Http.Headers;
using System.Text.Json;

namespace AdminConsole.Core.SharePoint;

/// <summary>
/// A SharePoint call that failed (M16, doc 14). SharePoint has two error shapes — REST's <c>odata.error</c> and CSOM's
/// <c>ErrorInfo</c> inside a 200 — and both end up here, so the caller handles one exception.
/// </summary>
public sealed class SharePointException : Exception
{
    public const string NetworkErrorCode = "Network";

    public SharePointException(int statusCode, string errorCode, string message, string? operation = null, string? requestId = null,
        TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Operation = operation;
        RequestId = requestId;
        RetryAfter = retryAfter;
    }

    public int StatusCode { get; }

    /// <summary>SharePoint's code, e.g. <c>-2147024891, System.UnauthorizedAccessException</c>, or a CSOM error code.</summary>
    public string ErrorCode { get; }

    /// <summary>What the console was doing: "Read OneDrive settings", "Set site admin".</summary>
    public string? Operation { get; }

    public string? RequestId { get; }
    public TimeSpan? RetryAfter { get; }

    public bool IsForbidden => StatusCode == 403 || ErrorCode.Contains("UnauthorizedAccessException", StringComparison.Ordinal);
    public bool IsNotFound => StatusCode == 404 || ErrorCode.Contains("FileNotFoundException", StringComparison.Ordinal);
    public bool IsNetwork => StatusCode == 0 && ErrorCode == NetworkErrorCode;

    /// <summary>The <c>odata.error</c> body of a failed REST call, or the status line when there is none.</summary>
    public static SharePointException FromResponse(int status, string? body, HttpResponseHeaders? headers, string? operation)
    {
        string code = $"HTTP{status}", message = $"SharePoint answered {status}.";
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var error = root.TryGetProperty("odata.error", out var o) ? o : root.TryGetProperty("error", out var e) ? e : default;
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString()!;
                    if (error.TryGetProperty("message", out var m))
                    {
                        message = m.ValueKind == JsonValueKind.String ? m.GetString()!
                            : m.TryGetProperty("value", out var v) ? v.GetString() ?? message : message;
                    }
                }
            }
            catch (JsonException)
            {
                message = body.Length > 300 ? body[..300] : body;
            }
        }

        var requestId = headers?.TryGetValues("request-id", out var ids) == true ? ids.FirstOrDefault()
            : headers?.TryGetValues("SPRequestGuid", out var guids) == true ? guids.FirstOrDefault() : null;
        return new SharePointException(status, code, message, operation, requestId, RetryDelay(headers));
    }

    public static TimeSpan? RetryDelay(HttpResponseHeaders? headers)
    {
        var retry = headers?.RetryAfter;
        if (retry?.Delta is { } delta) return delta;
        if (retry?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>The sentence an admin reads (UX §7); the raw error stays behind the Details expander.</summary>
    public static string Friendly(SharePointException ex) => ex switch
    {
        { IsNetwork: true } => "Can't reach SharePoint Online. Check the network and try again.",
        { IsForbidden: true } => "SharePoint refused: the signed-in account needs the SharePoint Administrator role for this.",
        { IsNotFound: true } => "SharePoint couldn't find that site.",
        _ when ex.Message.Contains("Additions to this Web site have been blocked", StringComparison.OrdinalIgnoreCase) =>
            "The OneDrive is locked, so SharePoint refuses changes to it. Unlock it first.",
        _ when ex.Message.Contains("does not exist or is not unique", StringComparison.OrdinalIgnoreCase) =>
            "SharePoint doesn't know that person. They need a SharePoint licence and an enabled account.",
        _ => ex.Message,
    };

    public static string Details(SharePointException ex) =>
        $"HTTP {ex.StatusCode}{(ex.Operation is null ? string.Empty : $" · {ex.Operation}")}\ncode: {ex.ErrorCode}\nmessage: {ex.Message}" +
        (ex.RequestId is null ? string.Empty : $"\nrequest-id: {ex.RequestId}");
}
