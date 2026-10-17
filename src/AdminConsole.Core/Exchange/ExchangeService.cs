using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// Exchange Online in the console's vocabulary (doc 13 §4): operations addressed by Entra object id, returning
/// projections rather than cmdlet output. The App never sees a cmdlet name.
/// </summary>
public sealed partial class ExchangeService
{
    private static readonly string[] UserSelect = ["ExternalDirectoryObjectId", "RecipientTypeDetails"];

    private static readonly string[] MailboxSummarySelect =
        ["ExternalDirectoryObjectId", "PrimarySmtpAddress", "RecipientTypeDetails", "HiddenFromAddressListsEnabled", "IsDirSynced"];

    private readonly ExchangeClient _client;
    private readonly TenantContext _tenant;
    private readonly ILogger<ExchangeService> _logger;

    public ExchangeService(ExchangeClient client, TenantContext tenant, ILogger<ExchangeService> logger)
    {
        _client = client;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>
    /// Whether this install may call Exchange at all: the permission is granted. Whether the admin holds an Exchange role
    /// is only known by asking, and a 403 then says so (doc 13 §5, "consent is not permission").
    /// </summary>
    public bool IsAvailable => _tenant.IsLoaded && _tenant.HasScope(Scopes.ExchangeManage);

    /// <summary>
    /// Every user Exchange knows, with its mailbox facts. <c>Get-User</c> is the only walk that includes users with no
    /// mailbox at all; <c>Get-Mailbox</c> adds the address and flags for those that have one. Rows with no Entra object
    /// id (the discovery mailbox) have nothing to join to and are left out.
    /// </summary>
    public async Task<IReadOnlyList<MailboxSummary>> GetMailboxSummariesAsync(CancellationToken ct)
    {
        var users = await _client.InvokeAsync("Get-User", Unlimited(), ct, UserSelect).ConfigureAwait(false);
        var mailboxes = await _client.InvokeAsync("Get-Mailbox", Unlimited(), ct, MailboxSummarySelect).ConfigureAwait(false);

        var byId = new Dictionary<string, MailboxSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in users.Items)
        {
            if (ExchangeValues.NonEmpty(row, "ExternalDirectoryObjectId") is not { } id) continue;
            byId[id] = new MailboxSummary { UserId = id, RecipientType = ExchangeValues.NonEmpty(row, "RecipientTypeDetails") ?? "User" };
        }

        foreach (var row in mailboxes.Items)
        {
            if (ExchangeValues.NonEmpty(row, "ExternalDirectoryObjectId") is not { } id) continue;
            byId[id] = new MailboxSummary
            {
                UserId = id,
                RecipientType = ExchangeValues.NonEmpty(row, "RecipientTypeDetails")
                                ?? (byId.TryGetValue(id, out var known) ? known.RecipientType : MailboxKinds.UserMailbox),
                PrimarySmtp = ExchangeValues.NonEmpty(row, "PrimarySmtpAddress"),
                HiddenFromAddressLists = ExchangeValues.IsTrue(row, "HiddenFromAddressListsEnabled"),
                DirSynced = ExchangeValues.IsTrue(row, "IsDirSynced"),
            };
        }

        _logger.LogInformation("Exchange: {Users} recipient(s), {Mailboxes} mailbox(es)", byId.Count, mailboxes.Items.Count);
        return [.. byId.Values];
    }

    private static Dictionary<string, object?> Unlimited() => new() { ["ResultSize"] = "Unlimited" };
}
