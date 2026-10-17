using System.Text.Json;

namespace AdminConsole.Core.Exchange;

/// <summary>How full a mailbox (or its archive) is, from <c>Get-MailboxStatistics</c>.</summary>
public sealed record MailboxUsage
{
    public static readonly IReadOnlyList<string> Select =
        ["TotalItemSize", "TotalDeletedItemSize", "ItemCount", "DeletedItemCount", "LastLogonTime", "LastUserActionTime"];

    public ExchangeSize TotalItemSize { get; init; }
    public ExchangeSize DeletedItemSize { get; init; }
    public long? ItemCount { get; init; }
    public long? DeletedItemCount { get; init; }

    /// <summary>Any access, including background ones from Outlook and mobile devices.</summary>
    public DateTimeOffset? LastLogon { get; init; }

    /// <summary>The last thing a person actually did in it — the better answer to "is anyone using this?".</summary>
    public DateTimeOffset? LastUserAction { get; init; }

    public static MailboxUsage FromJson(JsonElement row) => new()
    {
        TotalItemSize = ExchangeValues.Size(row, "TotalItemSize"),
        DeletedItemSize = ExchangeValues.Size(row, "TotalDeletedItemSize"),
        ItemCount = ExchangeValues.Long(row, "ItemCount"),
        DeletedItemCount = ExchangeValues.Long(row, "DeletedItemCount"),
        LastLogon = ExchangeValues.Date(row, "LastLogonTime"),
        LastUserAction = ExchangeValues.Date(row, "LastUserActionTime"),
    };
}
