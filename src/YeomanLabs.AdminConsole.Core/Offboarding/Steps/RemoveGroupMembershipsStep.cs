using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Groups;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Writes;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 6. Cloud groups only: dynamic membership is computed, synced groups are mastered on-prem, and a
/// role-assignable group needs Privileged Role Administrator. Each of those is listed as skipped with its reason
/// rather than attempted and failed, so the admin knows exactly what is left behind.
/// </summary>
public sealed class RemoveGroupMembershipsStep : IOffboardingStep
{
    private const string ScratchKey = "removable-groups";

    private readonly GroupService _groups;
    private readonly GraphClient _graph;
    private readonly TenantContext _tenant;

    public RemoveGroupMembershipsStep(GroupService groups, GraphClient graph, TenantContext tenant)
    {
        _groups = groups;
        _graph = graph;
        _tenant = tenant;
    }

    public string Id => "remove-group-memberships";
    public string Title => "Remove from groups";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.ChangeGroupMembership;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var groups = await ReadGroupsAsync(ctx.UserId, ct).ConfigureAwait(false);
        var privileged = _tenant.HasRole(DirectoryRoleRef.PrivilegedRoleAdministrator) || _tenant.IsGlobalAdmin;

        var removable = new List<DirectoryGroup>();
        var skipped = new List<string>();
        foreach (var g in groups)
        {
            var reason = _groups.MembershipBlockedReason(g, privileged);
            if (reason is null) removable.Add(g);
            else skipped.Add($"{Label(g)} — {reason}");
        }

        ctx.Set(ScratchKey, removable);

        if (removable.Count == 0)
        {
            return skipped.Count == 0
                ? StepPreview.NoOp(Id, "Not a member of any group")
                : StepPreview.NoOp(Id, $"Nothing to remove — all {skipped.Count} membership{(skipped.Count == 1 ? " is" : "s are")} managed elsewhere") with { Details = skipped };
        }

        var summary = $"Remove from {removable.Count} group{(removable.Count == 1 ? string.Empty : "s")}";
        if (skipped.Count > 0) summary += $" · {skipped.Count} skipped";

        return new StepPreview
        {
            StepId = Id,
            Summary = summary,
            Details = [.. removable.Select(Label), .. skipped],
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var removable = ctx.Get<List<DirectoryGroup>>(ScratchKey) ?? [];
        var removed = new List<(string Id, string Label)>();
        var failed = new List<string>();

        foreach (var group in removable)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _groups.RemoveMemberAsync(group.Id, ctx.UserId, ctx.UserLabel, ct).ConfigureAwait(false);
            if (outcome.Success) removed.Add((group.Id, Label(group)));
            else failed.Add($"{Label(group)} ({outcome.FriendlyError})");
        }

        Func<CancellationToken, Task<WriteOutcome>>? undo = removed.Count == 0
            ? null
            : async token =>
            {
                var back = 0;
                foreach (var (id, _) in removed)
                {
                    var outcome = await _groups.AddMemberAsync(id, ctx.UserId, ctx.UserLabel, token).ConfigureAwait(false);
                    if (outcome.Success) back++;
                }

                return new WriteOutcome
                {
                    Success = back == removed.Count,
                    Summary = $"Re-added to {back} of {removed.Count} group{(removed.Count == 1 ? string.Empty : "s")}",
                    FriendlyError = back == removed.Count ? null : "Some memberships could not be restored.",
                };
            };

        if (removed.Count == 0 && failed.Count > 0) return StepResult.Failed(Id, $"Could not leave any group: {string.Join("; ", failed)}");

        var message = $"Removed from {removed.Count} group{(removed.Count == 1 ? string.Empty : "s")}";
        if (failed.Count > 0) return StepResult.Failed(Id, $"{message}, but {failed.Count} failed: {string.Join("; ", failed)}") with { Undo = undo };
        return StepResult.Success(Id, message, undo);
    }

    /// <summary>Live, not cached: a membership added since the last delta must not be missed by an offboarding.</summary>
    private async Task<IReadOnlyList<DirectoryGroup>> ReadGroupsAsync(string userId, CancellationToken ct)
    {
        var page = await _graph.GetPageAsync($"users/{userId}/memberOf?$select={GraphSelects.MemberOf}&$top=999", ct).ConfigureAwait(false);
        return page.Items
            .Where(e => e.GetString("@odata.type")?.Contains("group", StringComparison.OrdinalIgnoreCase) != false)
            .Select(DirectoryGroup.FromJson)
            .ToList();
    }

    private static string Label(DirectoryGroup g) => g.DisplayName ?? g.Mail ?? g.Id;
}
