using AdminConsole.Core.Auth;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Takes a synced leaver out of their AD groups (M15.2). The cloud step (doc 06 step 6) skips synced groups because they
/// are mastered in AD; this is the step that empties them, and Entra Connect carries the change up.
/// </summary>
/// <remarks>
/// The primary group (Domain Users, usually) is not in <c>memberOf</c> and cannot be removed through a membership write
/// (M12.5), so it never appears in the plan and the preview says it stays. Built-in groups such as Domain Admins are
/// <em>not</em> skipped: taking a leaver out of them is the most important removal of all. They are marked, because
/// AD lets only a domain admin change them, and a refusal there fails that one group while the rest carry on.
/// Each removal is undoable, which is what Restore and the run's own Undo use.
/// </remarks>
public sealed class RemoveAdGroupMembershipsStep : IOffboardingStep
{
    private const string Plan = "onprem.groups.plan";
    private const int GroupLookupLimit = 2000;

    private readonly IDirectoryService _directory;

    public RemoveAdGroupMembershipsStep(IDirectoryService directory) => _directory = directory;

    public string Id => "remove-ad-group-memberships";
    public string Title => "Remove from AD groups";
    public StepSurface Surface => StepSurface.OnPremAd;
    public Capability? RequiredCapability => null;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (!ctx.User.OnPremisesSyncEnabled)
        {
            return StepPreview.NoOp(Id, $"{ctx.UserLabel} is cloud-only, so there are no AD groups to leave");
        }

        var found = await OnPremStep.FindAsync(_directory, ctx, ct).ConfigureAwait(false);
        if (found.Blocked is { } blocked) return StepPreview.Blocked(Id, Title, blocked);
        var user = found.User!;
        if (user.MemberOfDns.Count == 0)
        {
            return StepPreview.NoOp(Id, $"{user.SamAccountName} is in no AD groups beyond its primary group");
        }

        // memberOf holds distinguished names and writes address groups by GUID: one group list, matched by DN, the way
        // the AD object page does it (M12.11).
        var groups = await _directory.SearchGroupsAsync(string.Empty, GroupLookupLimit, null, ct).ConfigureAwait(false);
        var byDn = groups.ToDictionary(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase);
        var plan = new List<OnPremGroup>();
        var unresolved = new List<string>();
        foreach (var dn in user.MemberOfDns)
        {
            if (byDn.TryGetValue(dn, out var g)) plan.Add(g);
            else unresolved.Add(DistinguishedNames.FriendlyPath(dn));
        }

        ctx.Set(Plan, new GroupPlan(user, plan));
        return new StepPreview
        {
            StepId = Id,
            Summary = $"Remove {user.SamAccountName} from {plan.Count} AD group{(plan.Count == 1 ? string.Empty : "s")}"
                      + (unresolved.Count > 0 ? $" · {unresolved.Count} not found in the first {GroupLookupLimit} groups" : string.Empty),
            Details =
            [
                .. plan.Select(g => g.IsCritical ? $"{g.Primary} — built-in; AD lets only a domain admin change it" : g.Primary),
                .. unresolved.Select(u => $"{u} — couldn't be looked up; remove it in AD"),
                $"{PrimaryGroupName(user.PrimaryGroupId)} stays — it is the account's primary group, which every AD account must have",
            ],
            Reason = "Entra Connect carries these to the cloud at its next sync.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (ctx.Get<GroupPlan>(Plan) is not { } plan) return StepResult.Failed(Id, "The AD groups were not worked out; nothing was changed.");

        var removed = new List<OnPremGroup>();
        var failed = new List<string>();
        foreach (var g in plan.Groups)
        {
            var outcome = await _directory.RemoveFromGroupAsync(g.ObjectGuid, plan.User.ObjectGuid, ct).ConfigureAwait(false);
            if (outcome.Success) removed.Add(g);
            else failed.Add($"{g.Primary}: {outcome.FriendlyError ?? outcome.Summary}");
        }

        Func<CancellationToken, Task<WriteOutcome>>? undo = removed.Count == 0 ? null : async token =>
        {
            var back = 0;
            foreach (var g in removed)
            {
                var o = await _directory.AddToGroupAsync(g.ObjectGuid, plan.User.ObjectGuid, token).ConfigureAwait(false);
                if (o.Success) back++;
            }

            return new WriteOutcome { Success = back == removed.Count, Summary = $"Put {plan.User.SamAccountName} back in {back} of {removed.Count} AD groups" };
        };

        if (failed.Count == 0) return StepResult.Success(Id, $"Removed {plan.User.SamAccountName} from {removed.Count} AD group{(removed.Count == 1 ? string.Empty : "s")}", undo);
        var message = $"Removed from {removed.Count} of {plan.Groups.Count} AD groups. Not removed: {string.Join("; ", failed)}";
        return removed.Count > 0 ? StepResult.Success(Id, message, undo) : StepResult.Failed(Id, message);
    }

    /// <summary>The well-known primary groups by RID; anything else is named by its number, which is what AD stores.</summary>
    private static string PrimaryGroupName(int rid) => rid switch
    {
        513 => "Domain Users",
        514 => "Domain Guests",
        515 => "Domain Computers",
        512 => "Domain Admins",
        _ => $"Primary group {rid}",
    };

    private sealed record GroupPlan(OnPremUser User, IReadOnlyList<OnPremGroup> Groups);
}
