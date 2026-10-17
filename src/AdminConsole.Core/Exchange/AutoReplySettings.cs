using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// Automatic replies, from <c>Get-MailboxAutoReplyConfiguration</c>. The messages are mailbox content: shown on the page,
/// never written to a log (doc 13 §2).
/// </summary>
public sealed partial record AutoReplySettings
{
    /// <summary><c>Disabled</c>, <c>Enabled</c> or <c>Scheduled</c>.</summary>
    public string State { get; init; } = "Disabled";

    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }

    /// <summary>HTML, as Outlook stores it.</summary>
    public string? InternalMessage { get; init; }

    public string? ExternalMessage { get; init; }

    /// <summary><c>None</c>, <c>Known</c> (contacts only) or <c>All</c>.</summary>
    public string ExternalAudience { get; init; } = "None";

    public bool IsOn => !string.Equals(State, "Disabled", StringComparison.OrdinalIgnoreCase);
    public bool IsScheduled => string.Equals(State, "Scheduled", StringComparison.OrdinalIgnoreCase);

    public static AutoReplySettings FromJson(JsonElement row) => new()
    {
        State = ExchangeValues.NonEmpty(row, "AutoReplyState") ?? "Disabled",
        Start = ExchangeValues.Date(row, "StartTime"),
        End = ExchangeValues.Date(row, "EndTime"),
        InternalMessage = ExchangeValues.NonEmpty(row, "InternalMessage"),
        ExternalMessage = ExchangeValues.NonEmpty(row, "ExternalMessage"),
        ExternalAudience = ExchangeValues.NonEmpty(row, "ExternalAudience") ?? "None",
    };

    /// <summary>The message as plain text, for a preview line. Not a sanitiser — nothing here is ever rendered as HTML.</summary>
    public static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Tags().Replace(LineBreaks().Replace(html, "\n"), string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"<\s*(br|/p|/div)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\r\n ]+")]
    private static partial Regex Whitespace();
}
