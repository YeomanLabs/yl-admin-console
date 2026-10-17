using System.Text.Json;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// A default folder, found by its <c>FolderType</c>. Folder names are localised — "Kalender" in a German mailbox — so a
/// folder is never addressed by a hardcoded English name; the type finds it and the name it returns builds the identity
/// (doc 13 §2).
/// </summary>
public sealed record MailboxFolder
{
    /// <summary>The folder types the Mailbox tab offers for folder permissions, with the <c>-FolderScope</c> that finds each.</summary>
    public static readonly IReadOnlyList<(string FolderType, string Label)> Delegable =
    [
        ("Calendar", "Calendar"),
        ("Inbox", "Inbox"),
        ("Contacts", "Contacts"),
        ("Tasks", "Tasks"),
        ("SentItems", "Sent Items"),
        ("Drafts", "Drafts"),
    ];

    public required string FolderType { get; init; }

    /// <summary>The name in the mailbox's own language.</summary>
    public required string Name { get; init; }

    /// <summary><c>/Calendar</c>, or <c>/Inbox/Projects</c> for a subfolder.</summary>
    public required string Path { get; init; }

    /// <summary>The <c>-Identity</c> the folder-permission cmdlets take: <c>{object id}:\Calendar</c>.</summary>
    public string IdentityFor(string userId) => $"{userId}:\\{Path.TrimStart('/').Replace('/', '\\')}";

    /// <summary>The default folder of <paramref name="folderType"/> among a <c>-FolderScope</c> result, which also lists its subfolders.</summary>
    public static MailboxFolder? FindDefault(IEnumerable<JsonElement> rows, string folderType)
    {
        var row = rows.FirstOrDefault(r => string.Equals(ExchangeValues.String(r, "FolderType"), folderType, StringComparison.OrdinalIgnoreCase));
        if (row.ValueKind != JsonValueKind.Object) return null;
        var name = ExchangeValues.NonEmpty(row, "Name");
        var path = ExchangeValues.NonEmpty(row, "FolderPath") ?? (name is null ? null : "/" + name);
        return name is null || path is null ? null : new MailboxFolder { FolderType = folderType, Name = name, Path = path };
    }
}
