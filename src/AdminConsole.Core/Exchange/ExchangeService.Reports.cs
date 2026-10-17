namespace AdminConsole.Core.Exchange;

public sealed partial class ExchangeService
{
    private static readonly IReadOnlyList<string> DelegationSelect =
        ["ExternalDirectoryObjectId", "Identity", "Name", "DisplayName", "PrimarySmtpAddress", "RecipientTypeDetails", "GrantSendOnBehalfTo"];

    /// <summary>
    /// M19.3: every mailbox with its Send on Behalf list, and every explicit Send As grant in the tenant — two calls,
    /// whatever the size of the tenant. Send As rows name the mailbox by <c>Identity</c>, so the map is keyed by it.
    /// </summary>
    public async Task<DelegationOverview> GetDelegationOverviewAsync(CancellationToken ct)
    {
        var mailboxes = await _client.InvokeAsync("Get-Mailbox", Unlimited(), ct, DelegationSelect).ConfigureAwait(false);
        var list = new List<DelegationMailbox>();
        foreach (var row in mailboxes.Items)
        {
            if (ExchangeValues.NonEmpty(row, "ExternalDirectoryObjectId") is not { } id) continue;
            var identity = ExchangeValues.NonEmpty(row, "Identity") ?? ExchangeValues.NonEmpty(row, "Name") ?? id;
            list.Add(new DelegationMailbox(id, identity, ExchangeValues.NonEmpty(row, "DisplayName") ?? ExchangeValues.NonEmpty(row, "Name") ?? identity,
                ExchangeValues.NonEmpty(row, "PrimarySmtpAddress"), MailboxKinds.From(ExchangeValues.NonEmpty(row, "RecipientTypeDetails") ?? MailboxKinds.UserMailbox),
                ExchangeValues.Strings(row, "GrantSendOnBehalfTo")));
        }

        var sendAs = await _client.InvokeAsync("Get-RecipientPermission", Unlimited(), ct,
            ["Identity", "Trustee", "TrusteeSidString", "AccessRights", "IsInherited", "AccessControlType"]).ConfigureAwait(false);
        var byMailbox = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in sendAs.Items.GroupBy(r => ExchangeValues.NonEmpty(r, "Identity") ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var entries = MailboxPermissionEntry.FromSendAsRows(group).Where(e => !e.IsDeny).Select(e => e.Principal).ToList();
            if (group.Key.Length > 0 && entries.Count > 0) byMailbox[group.Key] = entries;
        }

        return new DelegationOverview(list, byMailbox);
    }
}
