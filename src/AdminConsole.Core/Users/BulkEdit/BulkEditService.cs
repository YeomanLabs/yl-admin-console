using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Users.BulkEdit;

/// <summary>
/// Sets the same fields on many users at once (M18.3), each written where it is mastered.
/// </summary>
/// <remarks>
/// <para>
/// A synced user's department lives in AD: a Graph PATCH of it is refused, and if it were not, Entra Connect would put
/// the old value back within half an hour. So the plan routes every property per person — AD for a synced user's
/// mastered fields, Graph for everything else — and one person can need both (department in AD, usage location in the
/// cloud). Only fields whose value differs are sent; a person already right is reported, not touched.
/// </para>
/// <para>
/// Every write in a run carries one correlation id, so the Activity page shows the whole edit as one thing and a
/// failure can be traced to the run that caused it.
/// </para>
/// </remarks>
public sealed class BulkEditService
{
    /// <summary>A few at a time: fast enough for a few hundred people, slow enough not to be throttled.</summary>
    private const int Parallelism = 4;

    private readonly UserService _users;
    private readonly CacheDb _db;
    private readonly IDirectoryService _directory;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;
    private readonly ILogger<BulkEditService> _logger;

    public BulkEditService(UserService users, CacheDb db, IDirectoryService directory, ActionLog actions, TenantContext tenant, ILogger<BulkEditService> logger)
    {
        _users = users;
        _db = db;
        _directory = directory;
        _actions = actions;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>The preview, from the cache. Nothing is sent.</summary>
    public BulkPlan Plan(IReadOnlyList<string> userIds, IReadOnlyList<BulkChange> changes)
    {
        var problems = new List<string>();
        var usable = new List<BulkChange>();
        foreach (var change in changes)
        {
            if (BulkField.All.FirstOrDefault(f => f.Property == change.Property) is null)
            {
                problems.Add($"{change.Property} cannot be set in bulk.");
                continue;
            }

            var value = string.IsNullOrWhiteSpace(change.Value) ? null : change.Value.Trim();
            if (change.Property == "usageLocation" && value is not null && (value.Length != 2 || !value.All(char.IsAsciiLetter)))
            {
                problems.Add("Usage location is a two-letter country code, such as GB or US.");
                continue;
            }

            usable.Add(change with { Value = change.Property == "usageLocation" ? value?.ToUpperInvariant() : value });
        }

        var users = _db.GetUsers(userIds);
        var rows = users
            .OrderBy(u => u.DisplayName ?? u.UserPrincipalName, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new BulkPlanRow(u, [.. usable.Select(c => PlanOne(u, c))]))
            .ToList();

        return new BulkPlan(rows, usable, problems);
    }

    private static BulkPlannedChange PlanOne(DirectoryUser user, BulkChange change)
    {
        var label = BulkField.All.First(f => f.Property == change.Property).Label;
        var from = UserService.CurrentText(user, change.Property);

        if (user.IsReadOnly(change.Property))
        {
            // Never "already set" from the cloud copy: it trails AD by up to a sync cycle, so a correction made within
            // half an hour of a bad edit would be skipped as already right. Replacing an AD value with itself is
            // harmless, so the AD half is always written.
            return OnPremAttributeMap.Refusal(change.Property) is { } refusal
                ? new BulkPlannedChange(change.Property, label, from, change.Value, BulkRoute.Refused, refusal)
                : new BulkPlannedChange(change.Property, label, from, change.Value, BulkRoute.ActiveDirectory);
        }

        return string.Equals(from ?? string.Empty, change.Value ?? string.Empty, StringComparison.Ordinal)
            ? new BulkPlannedChange(change.Property, label, from, change.Value, BulkRoute.Unchanged)
            : new BulkPlannedChange(change.Property, label, from, change.Value, BulkRoute.Entra);
    }

    /// <summary>Runs a plan. Results arrive through <paramref name="progress"/> as each person finishes.</summary>
    public async Task<IReadOnlyList<BulkUserResult>> RunAsync(BulkPlan plan, string correlationId, IProgress<BulkUserResult>? progress, CancellationToken ct)
    {
        using var scope = ActionLog.BeginCorrelation(correlationId);
        _logger.LogInformation("Bulk edit {Correlation}: {Count} users, {Fields} fields", correlationId, plan.Rows.Count, plan.Changes.Count);

        var needsAd = plan.Rows.Any(r => r.ToDirectory.Count > 0);
        var status = needsAd ? await _directory.GetStatusAsync(ct).ConfigureAwait(false) : null;

        var results = new BulkUserResult?[plan.Rows.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, plan.Rows.Count), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var result = await RunOneAsync(plan.Rows[i], status, token).ConfigureAwait(false);
                results[i] = result;
                progress?.Report(result);
            }).ConfigureAwait(false);

        return [.. results.Select(r => r!)];
    }

    private async Task<BulkUserResult> RunOneAsync(BulkPlanRow row, DirectoryStatus? status, CancellationToken ct)
    {
        var user = row.User;
        if (!row.HasWork)
        {
            var refused = row.Changes.Where(c => c.Route == BulkRoute.Refused).ToList();
            return refused.Count > 0
                ? new BulkUserResult(user.Id, row.Label, BulkStatus.Failed, string.Join(" ", refused.Select(c => c.Text)))
                : new BulkUserResult(user.Id, row.Label, BulkStatus.NothingToDo, "Already set");
        }

        var done = new List<string>();
        var failed = new List<string>();

        if (row.ToDirectory.Count > 0)
        {
            var (ok, text) = await WriteDirectoryAsync(row, status, ct).ConfigureAwait(false);
            (ok ? done : failed).Add(text);
        }

        if (row.ToEntra.Count > 0)
        {
            var values = row.ToEntra.ToDictionary(c => c.Property, c => (object?)c.To, StringComparer.Ordinal);
            try
            {
                var outcome = await _users.UpdateManyAsync(user.Id, values, ct).ConfigureAwait(false);
                if (outcome.Success || outcome.WasNoOp) done.Add(outcome.WasNoOp ? "Entra ID already set" : "Entra ID: " + outcome.Summary);
                else failed.Add("Entra ID: " + (outcome.FriendlyError ?? outcome.Summary));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Bulk edit of {UserId} failed in Entra", user.Id);
                failed.Add("Entra ID: " + ex.Message);
            }
        }

        foreach (var refused in row.Changes.Where(c => c.Route == BulkRoute.Refused)) failed.Add(refused.Text);

        var statusOf = failed.Count == 0 ? BulkStatus.Changed : done.Count == 0 ? BulkStatus.Failed : BulkStatus.PartlyDone;
        return new BulkUserResult(user.Id, row.Label, statusOf, string.Join(" · ", failed.Concat(done)));
    }

    private async Task<(bool Ok, string Text)> WriteDirectoryAsync(BulkPlanRow row, DirectoryStatus? status, CancellationToken ct)
    {
        if (status is not { IsConnected: true })
        {
            return (false, $"AD: {status?.Reason ?? "no domain controller reachable"}");
        }

        if (row.User.OnPremisesImmutableId is not { Length: > 0 } anchor)
        {
            return (false, "AD: this synced account has no source anchor, so its AD object cannot be found");
        }

        var onPrem = await _directory.GetUserByImmutableIdAsync(anchor, ct).ConfigureAwait(false);
        if (onPrem is null) return (false, "AD: no AD object matches this account's source anchor");

        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in row.ToDirectory)
        {
            var translation = OnPremAttributeMap.Translate(change.Property, change.To);
            if (!translation.CanWrite) return (false, $"AD: {change.Label}: {translation.Refusal}");
            attributes[translation.LdapName!] = translation.Value;
        }

        var outcome = await _directory.SetAttributesAsync(onPrem.ObjectGuid, attributes, ct).ConfigureAwait(false);
        var label = row.Label;
        var message = string.Join(" · ", row.ToDirectory.Select(c => c.Text));
        _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = ObjectType.User,
            // The AD object's GUID: this row records a change to the on-prem object (as the identity sheet's AD writes do).
            TargetId = onPrem.ObjectGuid.ToString(),
            TargetName = label,
            Action = "onprem.user.setAttribute",
            RequestSummary = $"LDAP modify {string.Join(", ", attributes.Keys)} on {onPrem.DistinguishedName} via {status.DomainController}",
            Outcome = outcome.Success ? ActionOutcome.Success : ActionOutcome.Failed,
            Message = outcome.Success ? message : outcome.FriendlyError ?? outcome.Summary,
        });

        if (!outcome.Success) return (false, $"AD: {outcome.FriendlyError ?? outcome.Summary}");
        return (true, outcome.WasNoOp ? "AD already set" : "AD: saved; the cloud copy follows at the next Entra Connect sync");
    }
}
