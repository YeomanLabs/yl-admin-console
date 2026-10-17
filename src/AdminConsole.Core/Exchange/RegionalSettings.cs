using System.Text.Json;

namespace AdminConsole.Core.Exchange;

/// <summary>A mailbox's language, time zone and formats, from <c>Get-MailboxRegionalConfiguration</c>.</summary>
public sealed record RegionalSettings
{
    public string? Language { get; init; }
    public string? TimeZone { get; init; }
    public string? DateFormat { get; init; }
    public string? TimeFormat { get; init; }

    /// <summary>Whether default folder names follow the language ("Posteingang" rather than "Inbox").</summary>
    public bool FolderNamesFollowLanguage { get; init; }

    /// <summary>A mailbox that has never been opened has no regional configuration yet.</summary>
    public bool IsSet => Language is not null || TimeZone is not null;

    public static RegionalSettings FromJson(JsonElement row) => new()
    {
        Language = ExchangeValues.NonEmpty(row, "Language"),
        TimeZone = ExchangeValues.NonEmpty(row, "TimeZone"),
        DateFormat = ExchangeValues.NonEmpty(row, "DateFormat"),
        TimeFormat = ExchangeValues.NonEmpty(row, "TimeFormat"),
        FolderNamesFollowLanguage = ExchangeValues.IsTrue(row, "DefaultFolderNameMatchingUserLanguage"),
    };
}
