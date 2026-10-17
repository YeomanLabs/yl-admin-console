using System.Text.Json;

namespace AdminConsole.Core.Exchange;

/// <summary>One person (or group) with access to a mailbox: Full Access from <c>Get-MailboxPermission</c>, Send As from
/// <c>Get-RecipientPermission</c>.</summary>
public sealed record MailboxPermissionEntry
{
    /// <summary>Who holds it, as Exchange names them — a UPN for a user, a name for a group.</summary>
    public required string Principal { get; init; }

    public IReadOnlyList<string> Rights { get; init; } = [];

    /// <summary>An explicit deny. Rare, and the one row an admin most needs to notice.</summary>
    public bool IsDeny { get; init; }

    /// <summary>
    /// The Full Access rows worth showing: the mailbox's own <c>NT AUTHORITY\SELF</c> entries and inherited
    /// organisation-level ones are dropped, because nobody granted them and nobody can remove them.
    /// </summary>
    public static IReadOnlyList<MailboxPermissionEntry> FromFullAccessRows(IEnumerable<JsonElement> rows) =>
    [
        .. rows
            .Where(r => !ExchangeValues.IsSelf(ExchangeValues.String(r, "User"), ExchangeValues.String(r, "UserSid")))
            .Where(r => !ExchangeValues.IsTrue(r, "IsInherited"))
            .Select(r => new MailboxPermissionEntry
            {
                Principal = ExchangeValues.NonEmpty(r, "User") ?? "(unknown)",
                Rights = ExchangeValues.Rights(r),
                IsDeny = ExchangeValues.IsTrue(r, "Deny"),
            })
            .OrderBy(e => e.Principal, StringComparer.OrdinalIgnoreCase),
    ];

    public static IReadOnlyList<MailboxPermissionEntry> FromSendAsRows(IEnumerable<JsonElement> rows) =>
    [
        .. rows
            .Where(r => !ExchangeValues.IsSelf(ExchangeValues.String(r, "Trustee"), ExchangeValues.String(r, "TrusteeSidString")))
            .Where(r => !ExchangeValues.IsTrue(r, "IsInherited"))
            .Select(r => new MailboxPermissionEntry
            {
                Principal = ExchangeValues.NonEmpty(r, "Trustee") ?? "(unknown)",
                Rights = ExchangeValues.Rights(r),
                IsDeny = string.Equals(ExchangeValues.String(r, "AccessControlType"), "Deny", StringComparison.OrdinalIgnoreCase),
            })
            .OrderBy(e => e.Principal, StringComparer.OrdinalIgnoreCase),
    ];
}
