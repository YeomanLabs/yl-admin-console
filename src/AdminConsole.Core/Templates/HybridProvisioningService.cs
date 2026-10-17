using System.Diagnostics;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Templates;

/// <summary>
/// Creating a person in AD from a hybrid template (M17.2): the AD half now — account, password, AD groups, addresses,
/// manager — and the cloud and mailbox halves queued for after Entra Connect (M17.3). In a hybrid organisation the AD
/// account is the person; creating them in the cloud first would make a second, unsynced account.
/// </summary>
public sealed class HybridProvisioningService
{
    private const int AdGroupLookupLimit = 2000;

    private readonly IDirectoryService _directory;
    private readonly PendingProvisioningService _pending;
    private readonly CacheDb _db;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;
    private readonly ILogger<HybridProvisioningService> _logger;

    public HybridProvisioningService(IDirectoryService directory, PendingProvisioningService pending, CacheDb db, ActionLog actions, TenantContext tenant,
        ILogger<HybridProvisioningService> logger)
    {
        _directory = directory;
        _pending = pending;
        _db = db;
        _actions = actions;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>Applies the template's naming rules to a person: <c>{first}</c>, <c>{last}</c>, <c>{alias}</c> and so on.</summary>
    public static string SamFor(UserTemplate template, string first, string last)
    {
        var rule = template.OnPrem?.SamRule ?? "{first}.{last}";
        var sam = NameRules.ExpandAddress(rule, first, last).Replace("@", string.Empty, StringComparison.Ordinal);
        return sam.Length > 20 ? sam[..20] : sam;
    }

    public async Task<CreateUserResult> CreateAsync(UserTemplate template, CreateUserRequest request, string ouDn, string samAccountName, string adUpn,
        CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var steps = new List<CreateStepResult>();
        var onPrem = template.OnPrem ?? throw new InvalidOperationException("Not a hybrid template.");
        var id = Guid.NewGuid().ToString("N");
        using var scope = ActionLog.BeginCorrelation($"hybrid-{id}");

        var extra = onPrem.AddressRules.Select(r => r.Replace("{alias}", request.MailNickname, StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mail = request.Attributes.TryGetValue("mail", out var m) ? m as string : null;
        var proxies = new List<string>();
        if (!string.IsNullOrWhiteSpace(mail)) proxies.Add($"SMTP:{mail}");
        proxies.AddRange(extra.Select(a => $"smtp:{a}"));

        var created = await _directory.CreateUserAsync(new NewAdUser
        {
            ParentOuDn = ouDn,
            Name = request.DisplayName,
            GivenName = request.GivenName,
            Surname = request.Surname,
            SamAccountName = samAccountName,
            UserPrincipalName = adUpn,
            Password = request.Password,
            MustChangePassword = request.ForceChangePasswordNextSignIn,
            Mail = mail,
            ProxyAddresses = proxies,
            Attributes = onPrem.Attributes,
        }, ct).ConfigureAwait(false);

        if (!created.Outcome.Success || created.User is not { } user)
        {
            Record(adUpn, request.DisplayName, "user.create.ad", ActionOutcome.Failed, created.Outcome.FriendlyError ?? created.Outcome.Summary);
            steps.Add(CreateStepResult.Fail("Create the account in AD", created.Outcome.FriendlyError ?? created.Outcome.Summary));
            return new CreateUserResult { Success = false, Steps = steps, Elapsed = clock.Elapsed };
        }

        Record(user.ObjectGuid.ToString(), request.DisplayName, "user.create.ad", ActionOutcome.Success, $"{created.Outcome.Summary} as {adUpn}");
        steps.Add(CreateStepResult.Ok("Create the account in AD", created.Outcome.Summary + (extra.Count > 0 ? $", with {string.Join(", ", extra)}" : string.Empty)));
        steps.Add(await AddAdGroupsAsync(user, onPrem.Groups, ct).ConfigureAwait(false));
        steps.Add(await SetAdManagerAsync(user, request.ManagerId, ct).ConfigureAwait(false));

        // Synced groups come with AD; only cloud groups wait for the cloud account.
        var cloudGroups = request.Groups.Where(g => _db.GetGroup(g.Id) is not { OnPremisesSyncEnabled: true }).ToList();
        var pending = new PendingProvisioning
        {
            Id = id,
            TenantId = _tenant.TenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = _tenant.SignedInUpn,
            DisplayName = request.DisplayName,
            AdUserPrincipalName = adUpn,
            SamAccountName = samAccountName,
            AdGuid = user.ObjectGuid,
            TemplateName = template.Name,
            UsageLocation = request.UsageLocation,
            SkuIds = request.SkuIds,
            Groups = cloudGroups,
            Exchange = template.Exchange,
        }.With($"Created in {DistinguishedNames.FriendlyPath(ouDn)} as {adUpn}");
        _pending.Enqueue(pending);

        var next = _pending.NextSyncDue is { } due && due > DateTimeOffset.UtcNow ? $"; the next cycle is due about {due.ToLocalTime():HH:mm}" : string.Empty;
        steps.Add(CreateStepResult.Ok("Wait for Entra Connect",
            $"Licences, {cloudGroups.Count} cloud group{(cloudGroups.Count == 1 ? string.Empty : "s")} and the mailbox settings follow once the account syncs{next}. Progress is on Home."));

        _logger.LogInformation("Created {Guid} in AD; pending {PendingId}", user.ObjectGuid, id);
        return new CreateUserResult
        {
            Success = true,
            UserId = null,
            UserPrincipalName = adUpn,
            Password = request.Password,
            Steps = steps,
            Elapsed = clock.Elapsed,
        };
    }

    private async Task<CreateStepResult> AddAdGroupsAsync(OnPremUser user, IReadOnlyList<TemplateAdGroup> groups, CancellationToken ct)
    {
        const string title = "Add to AD groups";
        if (groups.Count == 0) return CreateStepResult.Skip(title, "No AD groups in the template");

        var all = await _directory.SearchGroupsAsync(string.Empty, AdGroupLookupLimit, null, ct).ConfigureAwait(false);
        var byDn = all.ToDictionary(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var failed = new List<string>();
        foreach (var g in groups)
        {
            if (!byDn.TryGetValue(g.Dn, out var group))
            {
                failed.Add($"{g.Name ?? g.Dn} (not found)");
                continue;
            }

            var outcome = await _directory.AddToGroupAsync(group.ObjectGuid, user.ObjectGuid, ct).ConfigureAwait(false);
            if (outcome.Success) added.Add(group.Primary);
            else failed.Add($"{group.Primary} ({outcome.FriendlyError ?? outcome.Summary})");
        }

        var message = $"Added to {added.Count} AD group{(added.Count == 1 ? string.Empty : "s")}" + (added.Count > 0 ? $": {string.Join(", ", added)}" : string.Empty);
        return failed.Count == 0 ? CreateStepResult.Ok(title, message) : CreateStepResult.Fail(title, $"{message}; not added: {string.Join("; ", failed)}");
    }

    /// <summary>A synced user's manager is mastered in AD, so it is set there — when the manager has an AD account.</summary>
    private async Task<CreateStepResult> SetAdManagerAsync(OnPremUser user, string? managerId, CancellationToken ct)
    {
        const string title = "Set the manager in AD";
        if (string.IsNullOrWhiteSpace(managerId)) return CreateStepResult.Skip(title, "No manager in the template");
        var cloud = _db.GetUser(managerId);
        if (cloud?.OnPremisesImmutableId is not { Length: > 0 } anchor)
        {
            return CreateStepResult.Skip(title, $"{cloud?.DisplayName ?? "The manager"} has no AD account, so a synced user can't point at them; set a manager in AD");
        }

        var manager = await _directory.GetUserByImmutableIdAsync(anchor, ct).ConfigureAwait(false);
        if (manager is null) return CreateStepResult.Fail(title, $"{cloud.DisplayName}'s AD account could not be found");
        var outcome = await _directory.SetManagerAsync(user.ObjectGuid, manager.ObjectGuid, ct).ConfigureAwait(false);
        return outcome.Success ? CreateStepResult.Ok(title, $"Manager: {manager.Primary}") : CreateStepResult.Fail(title, outcome.FriendlyError ?? outcome.Summary);
    }

    private void Record(string targetId, string name, string action, ActionOutcome outcome, string message) =>
        _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = ObjectType.User,
            TargetId = targetId,
            TargetName = name,
            Action = action,
            RequestSummary = "LDAP add user",
            Outcome = outcome,
            Message = message,
        });
}
