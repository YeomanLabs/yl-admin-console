using Microsoft.Extensions.Logging;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.Users;

namespace AdminConsole.Core.Offboarding.Snapshot;

/// <summary>
/// Reads the before-picture of a leaver from Entra, AD and Exchange (M15.1). Graph is required — without it there is no
/// snapshot worth having and the run must not start. AD and Exchange are recorded when reachable and named as gaps when
/// not, because a tenant without either is a normal tenant.
/// </summary>
public sealed class SnapshotService
{
    /// <summary>The attributes the stamp step (doc 06) may overwrite, kept so Restore can put them back.</summary>
    public static readonly IReadOnlyList<string> StampedAttributes = ["employeeType", "department", "officeLocation", "jobTitle", "companyName"];

    private readonly UserService _users;
    private readonly CacheDb _db;
    private readonly IDirectoryService _directory;
    private readonly ExchangeService _exchange;
    private readonly ILogger<SnapshotService> _logger;

    public SnapshotService(UserService users, CacheDb db, IDirectoryService directory, ExchangeService exchange, ILogger<SnapshotService> logger)
    {
        _users = users;
        _db = db;
        _directory = directory;
        _exchange = exchange;
        _logger = logger;
    }

    /// <exception cref="GraphException">The Graph read failed: no snapshot, and the run must not go ahead.</exception>
    public async Task<OffboardingSnapshot> TakeAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var gaps = new List<string>();
        var live = await _users.HydrateAsync(ctx.UserId, ct).ConfigureAwait(false);
        if (live.Groups.Error is { } groupsError) throw groupsError;
        if (live.LicenseStates.Error is { } licenceError) throw licenceError;
        var user = live.User.Value ?? ctx.User;

        var skus = _db.GetSkus().ToDictionary(s => s.SkuId, s => s.DisplayName, StringComparer.OrdinalIgnoreCase);
        var attributes = new Dictionary<string, string?>();
        using (var raw = user.ParseRaw())
        {
            foreach (var name in StampedAttributes) attributes[name] = raw.RootElement.GetString(name);
        }

        string? usageLocation;
        using (var raw = user.ParseRaw()) usageLocation = raw.RootElement.GetString("usageLocation");

        if (live.Manager.Error is not null) gaps.Add("Entra: the manager could not be read.");
        if (live.AuthMethods.Error is not null) gaps.Add("Entra: authentication methods could not be counted.");

        return new OffboardingSnapshot
        {
            TenantId = ctx.Tenant.TenantId,
            UserId = user.Id,
            UserPrincipalName = user.UserPrincipalName,
            DisplayName = user.DisplayName,
            TakenAt = DateTimeOffset.UtcNow,
            TakenBy = ctx.Tenant.SignedInUpn,
            CorrelationId = ctx.CorrelationId,
            AccountEnabled = user.AccountEnabled,
            Synced = user.OnPremisesSyncEnabled,
            UsageLocation = usageLocation,
            Manager = live.Manager.Value is { } m ? new SnapshotRef(m.Id, m.DisplayName ?? m.UserPrincipalName) : null,
            Groups =
            [
                .. (live.Groups.Value ?? []).Select(g => new SnapshotGroup(g.Id, g.DisplayName, g.Kind.ToString(), g.OnPremisesSyncEnabled, g.IsDynamic,
                    g.Kind == GroupKind.DistributionOrMailSecurity)),
            ],
            Licences =
            [
                .. (live.LicenseStates.Value ?? []).Where(l => !l.IsGroupAssigned)
                    .Select(l => new SnapshotRef(l.SkuId, skus.TryGetValue(l.SkuId, out var n) ? n : null)),
            ],
            Attributes = attributes,
            AuthMethodCount = live.AuthMethods.Value?.Count ?? 0,
            Devices = [.. _users.GetCachedDevices(user.Id).Select(d => d.DeviceName ?? d.Id)],
            OnPrem = user.OnPremisesSyncEnabled ? await OnPremAsync(ctx, gaps, ct).ConfigureAwait(false) : null,
            Mailbox = await MailboxAsync(user.Id, gaps, ct).ConfigureAwait(false),
            Gaps = gaps,
        };
    }

    private async Task<SnapshotOnPrem?> OnPremAsync(OffboardingContext ctx, List<string> gaps, CancellationToken ct)
    {
        var found = await OnPremStep.FindAsync(_directory, ctx, ct).ConfigureAwait(false);
        if (found.User is not { } u)
        {
            gaps.Add($"AD: {found.Blocked}");
            return null;
        }

        return new SnapshotOnPrem
        {
            ObjectGuid = u.ObjectGuid,
            SamAccountName = u.SamAccountName,
            DistinguishedName = u.DistinguishedName,
            ParentDn = DistinguishedNames.Parent(u.DistinguishedName) ?? u.DistinguishedName,
            Enabled = u.Enabled,
            GroupDns = u.MemberOfDns,
            ManagerDn = u.ManagerDn,
        };
    }

    private async Task<SnapshotMailbox?> MailboxAsync(string userId, List<string> gaps, CancellationToken ct)
    {
        if (!_exchange.IsAvailable)
        {
            gaps.Add("Exchange: the Exchange Online permission isn't granted, so mailbox settings were not recorded.");
            return null;
        }

        var mailbox = await _exchange.GetMailboxAsync(userId, ct).ConfigureAwait(false);
        if (mailbox.IsNotFound) return null;
        if (!mailbox.IsSuccess)
        {
            gaps.Add($"Exchange: {mailbox.Message}");
            return null;
        }

        var m = mailbox.Value!;
        var fullAccess = await _exchange.GetFullAccessAsync(userId, ct).ConfigureAwait(false);
        var sendAs = await _exchange.GetSendAsAsync(userId, ct).ConfigureAwait(false);
        var autoReply = await _exchange.GetAutoReplyAsync(userId, ct).ConfigureAwait(false);
        IReadOnlyList<InboxRuleRef> rules = [];
        try
        {
            rules = await _exchange.GetInboxRulesAsync(userId, ct).ConfigureAwait(false);
        }
        catch (ExchangeException ex)
        {
            gaps.Add($"Exchange: inbox rules could not be read — {ExchangeErrorText.Friendly(ex)}");
        }

        if (!fullAccess.IsSuccess) gaps.Add($"Exchange: Full Access could not be read — {fullAccess.Message}");
        if (!sendAs.IsSuccess) gaps.Add($"Exchange: Send As could not be read — {sendAs.Message}");

        return new SnapshotMailbox
        {
            RecipientType = m.RecipientType,
            HiddenFromAddressLists = m.HiddenFromAddressLists,
            ForwardingAddress = m.ForwardingAddress,
            ForwardingSmtpAddress = m.ForwardingSmtpAddress,
            DeliverToMailboxAndForward = m.DeliverToMailboxAndForward,
            LitigationHold = m.LitigationHold,
            AutoReplyState = autoReply.Value?.State,
            SendOnBehalf = m.SendOnBehalf,
            FullAccess = [.. (fullAccess.Value ?? []).Where(e => !e.IsDeny).Select(e => e.Principal)],
            SendAs = [.. (sendAs.Value ?? []).Where(e => !e.IsDeny).Select(e => e.Principal)],
            EnabledInboxRules = [.. rules.Where(r => r.Enabled).Select(r => r.Name)],
        };
    }
}
