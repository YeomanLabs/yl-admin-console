using System.Text.Json;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Writes;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 16. A Microsoft 365 group with no owner is a group nobody can administer, and a leaver who was its only
/// owner leaves exactly that. The delegate is added as owner first, then the leaver is removed, so the group is never
/// ownerless even for a moment. Groups with another owner are left alone.
/// </summary>
public sealed class RemoveTeamsOwnershipStep : IOffboardingStep
{
    private const string ScratchKey = "sole-owned-groups";

    private readonly GraphClient _graph;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;

    public RemoveTeamsOwnershipStep(GraphClient graph, ActionLog actions, TenantContext tenant)
    {
        _graph = graph;
        _actions = actions;
        _tenant = tenant;
    }

    public string Id => "remove-teams-ownership";
    public string Title => "Transfer Teams and Microsoft 365 group ownership";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.EditGroup;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var owned = await ReadOwnedGroupsAsync(ctx.UserId, ct).ConfigureAwait(false);
        if (owned.Count == 0) return StepPreview.NoOp(Id, "Not the owner of any group");

        var counts = await CountOwnersAsync(owned.Select(g => g.Id).ToList(), ct).ConfigureAwait(false);
        var sole = owned.Where(g => counts.GetValueOrDefault(g.Id, 0) <= 1).ToList();
        var shared = owned.Count - sole.Count;
        ctx.Set(ScratchKey, sole);

        if (sole.Count == 0)
        {
            return StepPreview.NoOp(Id, $"Every one of the {owned.Count} owned group{(owned.Count == 1 ? " has" : "s has")} another owner — nothing would be left ownerless");
        }

        if (ctx.Delegate is null)
        {
            return StepPreview.Blocked(Id, $"Transfer ownership of {sole.Count} group{(sole.Count == 1 ? string.Empty : "s")}", "Choose a delegate first — ownership has to go to someone")
                with { Details = sole.Select(Label).ToList() };
        }

        var summary = $"Transfer {sole.Count} group{(sole.Count == 1 ? string.Empty : "s")} to {ctx.Delegate.Label}";
        if (shared > 0) summary += $" · {shared} left alone (another owner remains)";

        return new StepPreview
        {
            StepId = Id,
            Summary = summary,
            Details = sole.Select(Label).ToList(),
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (ctx.Delegate is not { } newOwner) return StepResult.Skipped(Id, "No delegate was chosen");

        var sole = ctx.Get<List<DirectoryGroup>>(ScratchKey) ?? [];
        var transferred = new List<(string Id, string Label)>();
        var failed = new List<string>();

        foreach (var group in sole)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Add first, then remove: a group must never be ownerless, not even between two calls.
                await AddOwnerAsync(group.Id, newOwner.Id, ct).ConfigureAwait(false);
                await _graph.SendAsync(HttpMethod.Delete, $"groups/{group.Id}/owners/{ctx.UserId}/$ref", null, ct).ConfigureAwait(false);
                Record(group, ActionOutcome.Success, $"Ownership transferred to {newOwner.Label}");
                transferred.Add((group.Id, Label(group)));
            }
            catch (GraphException ex)
            {
                Record(group, ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId);
                failed.Add($"{Label(group)} ({GraphErrorText.Friendly(ex)})");
            }
        }

        // Partially reversible (doc 06): the leaver can be made an owner again, but the delegate stays one, because
        // removing them could leave the group ownerless if the re-add failed.
        Func<CancellationToken, Task<WriteOutcome>>? undo = transferred.Count == 0
            ? null
            : async token =>
            {
                var back = 0;
                foreach (var (id, _) in transferred)
                {
                    try
                    {
                        await AddOwnerAsync(id, ctx.UserId, token).ConfigureAwait(false);
                        back++;
                    }
                    catch (GraphException)
                    {
                        // counted as a failure below
                    }
                }

                return new WriteOutcome
                {
                    Success = back == transferred.Count,
                    Summary = $"Made the user an owner again on {back} of {transferred.Count} group{(transferred.Count == 1 ? string.Empty : "s")} · {newOwner.Label} stays an owner",
                    FriendlyError = back == transferred.Count ? null : "Some ownerships could not be restored.",
                };
            };

        if (transferred.Count == 0 && failed.Count > 0) return StepResult.Failed(Id, $"Could not transfer any group: {string.Join("; ", failed)}");

        var message = $"Transferred {transferred.Count} group{(transferred.Count == 1 ? string.Empty : "s")} to {newOwner.Label}";
        if (failed.Count > 0) return StepResult.Failed(Id, $"{message}, but {failed.Count} failed: {string.Join("; ", failed)}") with { Undo = undo };
        return StepResult.Success(Id, message, undo);
    }

    private Task AddOwnerAsync(string groupId, string userId, CancellationToken ct) =>
        _graph.SendAsync(HttpMethod.Post, $"groups/{groupId}/owners/$ref",
            new Dictionary<string, object?> { ["@odata.id"] = $"{GraphClient.V1BaseAddress}users/{userId}" }, ct);

    private async Task<IReadOnlyList<DirectoryGroup>> ReadOwnedGroupsAsync(string userId, CancellationToken ct)
    {
        var page = await _graph.GetPageAsync($"users/{userId}/ownedObjects/microsoft.graph.group?$select={GraphSelects.MemberOf}&$top=999", ct).ConfigureAwait(false);
        return page.Items.Select(DirectoryGroup.FromJson).Where(g => g.Kind == GroupKind.M365).ToList();
    }

    /// <summary>One batch of owner counts; <c>$count</c> is not available on the owners navigation, so the ids are read.</summary>
    private async Task<IReadOnlyDictionary<string, int>> CountOwnersAsync(IReadOnlyList<string> groupIds, CancellationToken ct)
    {
        var requests = groupIds.Select(id => BatchRequest.Get(id, $"/groups/{id}/owners?$select=id&$top=10")).ToList();
        var responses = await _graph.BatchAsync(requests, ct).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (id, response) in responses)
        {
            if (!response.IsSuccess || response.Body is not { } body || !body.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                // Unknown owner count: treat as "has another owner" so the step never transfers a group it misread.
                counts[id] = int.MaxValue;
                continue;
            }

            counts[id] = value.GetArrayLength();
        }

        return counts;
    }

    private void Record(DirectoryGroup group, ActionOutcome outcome, string message, string? requestId = null) =>
        _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = ObjectType.Group,
            TargetId = group.Id,
            TargetName = Label(group),
            Action = "group.owner.transfer",
            RequestSummary = $"POST owners/$ref then DELETE owners/$ref on {group.Id}",
            Outcome = outcome,
            Message = message,
            GraphRequestId = requestId,
        });

    private static string Label(DirectoryGroup g) => g.DisplayName ?? g.Mail ?? g.Id;
}
