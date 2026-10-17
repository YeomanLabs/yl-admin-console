using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Exchange;

/// <summary>The Mailbox tab's reads (M13.5). Each is one section that loads and fails on its own.</summary>
public sealed partial class ExchangeService
{
    public Task<ExchangeSection<MailboxDetail>> GetMailboxAsync(string userId, CancellationToken ct) =>
        Section(async () =>
        {
            var result = await _client.InvokeAsync("Get-Mailbox", Identity(userId), ct, MailboxDetail.Select, _client.AnchorFor(userId)).ConfigureAwait(false);
            return result.First is { } row
                ? MailboxDetail.FromJson(userId, row)
                : throw new ExchangeException(404, "NotFound", "Exchange returned no mailbox.", "ManagementObjectNotFoundException", cmdlet: "Get-Mailbox");
        });

    /// <param name="archive">The archive's statistics rather than the primary mailbox's.</param>
    public Task<ExchangeSection<MailboxUsage>> GetUsageAsync(string userId, bool archive, CancellationToken ct) =>
        Section(async () =>
        {
            var parameters = Identity(userId);
            if (archive) parameters["Archive"] = true;
            var result = await _client.InvokeAsync("Get-MailboxStatistics", parameters, ct, MailboxUsage.Select, _client.AnchorFor(userId)).ConfigureAwait(false);
            return result.First is { } row ? MailboxUsage.FromJson(row) : new MailboxUsage();
        });

    public Task<ExchangeSection<IReadOnlyList<MailboxPermissionEntry>>> GetFullAccessAsync(string userId, CancellationToken ct) =>
        Section(async () =>
        {
            var result = await _client.InvokeAsync("Get-MailboxPermission", Identity(userId), ct, anchorMailbox: _client.AnchorFor(userId)).ConfigureAwait(false);
            return MailboxPermissionEntry.FromFullAccessRows(result.Items);
        });

    public Task<ExchangeSection<IReadOnlyList<MailboxPermissionEntry>>> GetSendAsAsync(string userId, CancellationToken ct) =>
        Section(async () =>
        {
            var result = await _client.InvokeAsync("Get-RecipientPermission", Identity(userId), ct, anchorMailbox: _client.AnchorFor(userId)).ConfigureAwait(false);
            return MailboxPermissionEntry.FromSendAsRows(result.Items);
        });

    public Task<ExchangeSection<RegionalSettings>> GetRegionalAsync(string userId, CancellationToken ct) =>
        Section(async () =>
        {
            var result = await _client.InvokeAsync("Get-MailboxRegionalConfiguration", Identity(userId), ct, anchorMailbox: _client.AnchorFor(userId)).ConfigureAwait(false);
            return result.First is { } row ? RegionalSettings.FromJson(row) : new RegionalSettings();
        });

    public Task<ExchangeSection<AutoReplySettings>> GetAutoReplyAsync(string userId, CancellationToken ct) =>
        Section(async () =>
        {
            var result = await _client.InvokeAsync("Get-MailboxAutoReplyConfiguration", Identity(userId), ct, anchorMailbox: _client.AnchorFor(userId)).ConfigureAwait(false);
            return result.First is { } row ? AutoReplySettings.FromJson(row) : new AutoReplySettings();
        });

    /// <summary>
    /// Who may do what in one default folder. Two calls: <c>Get-MailboxFolderStatistics -FolderScope</c> finds the folder
    /// by type, because its name is in the mailbox's language, then <c>Get-MailboxFolderPermission</c> reads it.
    /// </summary>
    public Task<ExchangeSection<FolderPermissions>> GetFolderPermissionsAsync(string userId, string folderType, CancellationToken ct) =>
        Section(async () =>
        {
            var parameters = Identity(userId);
            parameters["FolderScope"] = folderType;
            var folders = await _client.InvokeAsync("Get-MailboxFolderStatistics", parameters, ct, ["Name", "FolderPath", "FolderType"], _client.AnchorFor(userId)).ConfigureAwait(false);
            var folder = MailboxFolder.FindDefault(folders.Items, folderType)
                         ?? throw new ExchangeException(404, "NotFound", $"This mailbox has no {folderType} folder.", "ManagementObjectNotFoundException",
                             cmdlet: "Get-MailboxFolderStatistics");

            var permissions = await _client.InvokeAsync("Get-MailboxFolderPermission",
                new Dictionary<string, object?> { ["Identity"] = folder.IdentityFor(userId) }, ct, anchorMailbox: _client.AnchorFor(userId)).ConfigureAwait(false);
            return new FolderPermissions { Folder = folder, Entries = [.. permissions.Items.Select(FolderPermissionEntry.FromJson)] };
        });

    private static Dictionary<string, object?> Identity(string userId) => new() { ["Identity"] = userId };

    private async Task<ExchangeSection<T>> Section<T>(Func<Task<T>> read)
    {
        try
        {
            return ExchangeSection<T>.Ok(await read().ConfigureAwait(false));
        }
        catch (ExchangeException ex)
        {
            return ExchangeSection<T>.Failed(ex);
        }
        catch (SignInRequiredException)
        {
            return ExchangeSection<T>.Failed(new ExchangeException(0, NotGrantedCode,
                "The Exchange Online permission isn't granted for this app yet."));
        }
    }

    /// <summary>The error code a section carries when there is no Exchange token at all.</summary>
    public const string NotGrantedCode = "NotGranted";
}
