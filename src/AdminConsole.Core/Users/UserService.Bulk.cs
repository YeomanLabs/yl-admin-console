using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Users;

public sealed partial class UserService
{
    /// <summary>
    /// PATCHes several properties of one user in one request (M18.3). The same guards as <see cref="UpdateAsync"/> —
    /// on-prem-mastered properties are refused, values already in place are left out — but one PATCH, one re-read and
    /// one action-log row, so a bulk edit of two hundred people is two hundred requests rather than six hundred.
    /// </summary>
    public async Task<WriteOutcome> UpdateManyAsync(string userId, IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        var user = _db.GetUser(userId);
        var label = user?.DisplayName ?? user?.UserPrincipalName ?? userId;
        var summary = $"Update {label}";

        var refused = values.Keys.Where(p => user?.IsReadOnly(p) == true).ToList();
        if (refused.Count > 0)
        {
            var reason = $"{string.Join(", ", refused.Select(FriendlyProperty))}: {DirectoryUser.HybridReadOnlyReason}";
            _actions.Record(NewAction(userId, label, "user.patch", $"PATCH {string.Join(",", refused)}", ActionOutcome.Skipped, reason));
            return new WriteOutcome { Success = false, Summary = summary, FriendlyError = reason };
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        var previous = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (property, value) in values)
        {
            var was = user is null ? null : ReadProperty(user, property);
            if (JsonEquals(Blank(was) ? null : was, Blank(value) ? null : value)) continue;
            body[property] = Blank(value) ? null : value;
            previous[property] = was;
        }

        if (body.Count == 0) return WriteOutcome.NoOp("Already set to those values");

        var request = "PATCH " + string.Join(", ", body.Select(kv => IsSecret(kv.Key) ? $"{kv.Key}=***" : $"{kv.Key}={Describe(kv.Value)}"));
        try
        {
            await _graph.SendAsync(HttpMethod.Patch, $"users/{userId}", body, ct).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            var id = _actions.Record(NewAction(userId, label, "user.patch", request, ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("Bulk PATCH on {UserId} failed: {Code}", userId, ex.ErrorCode);
            return WriteOutcome.Failed(summary, ex, id, Capabilities.EditUser.RoleHint);
        }

        await WriteThroughAsync(userId, ct).ConfigureAwait(false);

        var message = string.Join(" · ", body.Select(kv => $"{FriendlyProperty(kv.Key)} → {Describe(kv.Value)}"));
        var inverse = JsonSerializer.Serialize(previous, GraphJson.Options);
        var actionId = _actions.Record(NewAction(userId, label, "user.patch", request, ActionOutcome.Success, message, null, "user.patch", inverse));
        _logger.LogInformation("Patched {Count} properties on {UserId}", body.Count, userId);

        return new WriteOutcome
        {
            Success = true,
            Summary = message,
            ActionId = actionId,
            Undo = async token =>
            {
                var back = await UpdateManyAsync(userId, previous, token).ConfigureAwait(false);
                if (back.Success) _actions.MarkUndone(actionId);
                return back with { Summary = $"Restored {label}" };
            },
        };
    }

    /// <summary>The current value of a property as the cache holds it, as text, for a bulk edit's preview.</summary>
    public static string? CurrentText(DirectoryUser user, string propertyName) => ReadProperty(user, propertyName) switch
    {
        null => null,
        string s => s.Length == 0 ? null : s,
        object o => Describe(o).Trim('"'),
    };
}
