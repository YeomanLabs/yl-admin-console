namespace AdminConsole.Core.Graph;

/// <summary>
/// UX §7: Graph error → friendly text. Extend as errors are encountered in the field. The raw error always stays
/// available behind the Details expander, so this only has to be helpful, never complete.
/// </summary>
public static class GraphErrorText
{
    public static string Friendly(GraphException ex, string? roleHint = null)
    {
        if (ex.StatusCode == 0 && ex.ErrorCode == GraphException.NetworkErrorCode)
        {
            return "Can't reach Microsoft Graph. Working from cached data.";
        }

        if (ex.IsThrottled || ex.StatusCode is 503 or 504)
        {
            var seconds = ex.RetryAfter is { } r ? (int)Math.Ceiling(r.TotalSeconds) : 30;
            return $"Microsoft Graph is throttling requests. Retrying in {seconds} s.";
        }

        var message = ex.Message ?? string.Empty;

        // Intune's message carries a support operation id and the whole request URL after a dash. The first sentence
        // is the only part an admin can act on; the rest stays behind the Details expander.
        if (ex.ErrorCode == GraphException.IntuneErrorCode)
        {
            var cut = message.IndexOf(" - Operation ID", StringComparison.OrdinalIgnoreCase);
            var head = cut > 0 ? message[..cut] : message;
            return head.Length == 0 ? "Intune refused the request." : $"Intune refused the request: {head}.";
        }

        switch (ex.ErrorCode)
        {
            case "Request_ResourceNotFound":
            case "ResourceNotFound":
                return "That object no longer exists. The cache will update on next sync.";

            case "Authorization_RequestDenied":
                return roleHint is null
                    ? "You don't have permission for this."
                    : $"You don't have permission for this ({roleHint}).";

            case "Request_BadRequest":
                if (message.Contains("usageLocation", StringComparison.OrdinalIgnoreCase))
                {
                    return "Set a usage location before assigning a license.";
                }

                // Graph words this at least two ways: "There are no available licenses" and
                // "Subscription with SKU ... does not have any available licenses." Match the part they share.
                if (message.Contains("available licenses", StringComparison.OrdinalIgnoreCase) || message.Contains("CountViolation", StringComparison.OrdinalIgnoreCase))
                {
                    return "No seats left for this licence.";
                }

                if (message.Contains("on-premises mastered", StringComparison.OrdinalIgnoreCase) || message.Contains("Directory Sync", StringComparison.OrdinalIgnoreCase))
                {
                    return "This property is mastered in on-prem AD and can't be changed here.";
                }

                return message.Length > 0 ? message : "Microsoft Graph rejected the request.";

            default:
                return message.Length > 0 ? message : $"Microsoft Graph returned HTTP {ex.StatusCode}.";
        }
    }

    /// <summary>The Details expander body (UX §7): status, code, request-id, timestamp, raw message.</summary>
    public static string Details(GraphException ex) =>
        $"HTTP {ex.StatusCode} {ex.ErrorCode}\nrequest-id: {ex.RequestId ?? "n/a"}\nclient-request-id: {ex.ClientRequestId ?? "n/a"}\n{ex.Timestamp:u}\n\n{ex.RawBody}";
}
