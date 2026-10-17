namespace AdminConsole.Core.Exchange;

/// <summary>
/// UX §7 for Exchange: a failed cmdlet in words an admin can act on. Keys on the server-side exception type, the only
/// stable part of an Exchange error (doc 13 §1). The raw error stays behind the Details expander, so this only has to be
/// helpful, never complete.
/// </summary>
public static class ExchangeErrorText
{
    public const string RoleHint = "Exchange Administrator, Exchange Recipient Administrator or Global Administrator can do this";

    public static string Friendly(ExchangeException ex)
    {
        if (ex.IsNetwork) return "Can't reach Exchange Online. Check the network connection and try again.";

        if (ex.IsThrottled || ex.StatusCode is 503 or 504)
        {
            var seconds = ex.RetryAfter is { } r && r < TimeSpan.FromHours(1) ? (int)Math.Ceiling(r.TotalSeconds) : 60;
            return $"Exchange Online is throttling requests. Try again in about {seconds} s.";
        }

        if (ex.IsUnauthorized)
        {
            return "Exchange Online did not accept the sign-in. Sign out and back in; if it persists, the Exchange permission may not be granted for this app.";
        }

        var text = ex.Message ?? string.Empty;

        // Exchange refuses on-prem-mastered attributes of a synced object. Its wording, not its type, is what is stable
        // here: the type is a generic task exception shared with many unrelated refusals.
        if (text.Contains("synchronized from your on-premises", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("being synchronized from", StringComparison.OrdinalIgnoreCase))
        {
            return "This mailbox is synced from on-prem AD, and Exchange Online won't change this setting for it. Change it in AD and let it sync.";
        }

        switch (ex.ExceptionName)
        {
            case "ManagementObjectNotFoundException":
                return "Exchange can't find that mailbox or recipient. It may have no mailbox, or it was created moments ago and Exchange hasn't caught up.";

            case "ManagementObjectAmbiguousException":
                return "More than one Exchange object matches. This is a bug in the console — it should always address an object by its id.";

            case "AmbiguousParameterSetException":
            case "ParameterBindingException":
            case "ParameterBindingValidationException":
                return "Exchange didn't accept the request the console sent. This is a bug in the console, not something you did.";
        }

        if (ex.IsForbidden || text.Contains("isn't within your current write scope", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return $"Your account doesn't have an Exchange role that allows this. {RoleHint}.";
        }

        return text.Length == 0 ? $"Exchange Online refused the request (HTTP {ex.StatusCode})." : text;
    }
}
