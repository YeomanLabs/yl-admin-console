using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Groups;

/// <summary>
/// Group membership writes (doc 04 §Groups). The skip rules are enforced here, not just in the UI: dynamic groups,
/// on-prem synced groups and role-assignable groups without the right role are refused before Graph is called.
/// </summary>
public sealed partial class GroupService
{
    /// <summary>Adds a member. Undo removes it again.</summary>
    public Task<WriteOutcome> AddMemberAsync(string groupId, string memberId, string memberLabel, CancellationToken ct) =>
        ChangeMemberAsync(groupId, memberId, memberLabel, add: true, ct);

    /// <summary>Removes a member. Undo adds it back.</summary>
    public Task<WriteOutcome> RemoveMemberAsync(string groupId, string memberId, string memberLabel, CancellationToken ct) =>
        ChangeMemberAsync(groupId, memberId, memberLabel, add: false, ct);

    /// <summary>
    /// Doc 04 skip rules. Returns the reason membership cannot be changed, or null when it can.
    /// Role-assignable groups need Privileged Role Administrator, which the caller reports.
    /// </summary>
    public string? MembershipBlockedReason(DirectoryGroup group, bool hasPrivilegedRoleAdmin) =>
        group.MembershipChangeBlockedReason(hasPrivilegedRoleAdmin);

    private async Task<WriteOutcome> ChangeMemberAsync(string groupId, string memberId, string memberLabel, bool add, CancellationToken ct)
    {
        var group = _db.GetGroup(groupId);
        var groupLabel = group?.DisplayName ?? groupId;
        var verb = add ? "Added" : "Removed";
        var summary = $"{verb} {memberLabel} {(add ? "to" : "from")} {groupLabel}";
        var action = add ? "group.member.add" : "group.member.remove";

        if (group is not null && group.MembershipChangeBlockedReason(_tenant.HasRole(DirectoryRoleRef.PrivilegedRoleAdministrator) || _tenant.IsGlobalAdmin) is { } blocked)
        {
            _actions.Record(NewAction(groupId, groupLabel, action, $"{(add ? "POST" : "DELETE")} members {memberId}", ActionOutcome.Skipped, blocked));
            _logger.LogInformation("Refused membership change on {GroupId}: {Reason}", groupId, blocked);
            return new WriteOutcome { Success = false, Summary = summary, FriendlyError = blocked };
        }

        try
        {
            if (add)
            {
                var body = new Dictionary<string, object?> { ["@odata.id"] = $"{GraphClient.V1BaseAddress}directoryObjects/{memberId}" };
                await _graph.SendAsync(HttpMethod.Post, $"groups/{groupId}/members/$ref", body, ct).ConfigureAwait(false);
            }
            else
            {
                await _graph.SendAsync(HttpMethod.Delete, $"groups/{groupId}/members/{memberId}/$ref", null, ct).ConfigureAwait(false);
            }
        }
        catch (GraphException ex)
        {
            var id = _actions.Record(NewAction(groupId, groupLabel, action, $"{(add ? "POST" : "DELETE")} members {memberId}", ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("{Action} {MemberId} on {GroupId} failed: {Code}", action, memberId, groupId, ex.ErrorCode);
            return WriteOutcome.Failed(summary, ex, id, Capabilities.ChangeGroupMembership.RoleHint);
        }

        // Write-through: the membership tables drive the Access tab and the group page member counts.
        _db.Write(w =>
        {
            if (add) w.AddGroupMember(groupId, memberId, "user");
            else w.RemoveGroupMember(groupId, memberId);
            w.RefreshMemberCount(groupId);
        });

        var inverse = JsonSerializer.Serialize(new { groupId, memberId, add = !add }, GraphJson.Options);
        var actionId = _actions.Record(NewAction(groupId, groupLabel, action, $"{(add ? "POST" : "DELETE")} members {memberId}", ActionOutcome.Success, summary, null, add ? "group.member.remove" : "group.member.add", inverse));
        _logger.LogInformation("{Action} {MemberId} on {GroupId}", action, memberId, groupId);

        return new WriteOutcome
        {
            Success = true,
            Summary = summary,
            ActionId = actionId,
            Undo = async token =>
            {
                var back = await ChangeMemberAsync(groupId, memberId, memberLabel, !add, token).ConfigureAwait(false);
                if (back.Success) _actions.MarkUndone(actionId);
                return back;
            },
        };
    }

    private ActionRecord NewAction(string groupId, string groupLabel, string action, string request, ActionOutcome outcome, string? message = null, string? requestId = null, string? inverseAction = null, string? inversePayload = null) => new()
    {
        Actor = _tenant.SignedInUpn,
        TenantId = _tenant.TenantId,
        TargetType = ObjectType.Group,
        TargetId = groupId,
        TargetName = groupLabel,
        Action = action,
        RequestSummary = request,
        Outcome = outcome,
        Message = message,
        GraphRequestId = requestId,
        InverseAction = inverseAction,
        InversePayload = inversePayload,
    };
}
