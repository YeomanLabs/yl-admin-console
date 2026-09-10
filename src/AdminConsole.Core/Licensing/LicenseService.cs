using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Licensing;

/// <summary>
/// Licence assignment (doc 04 §Users <c>assignLicense</c>). Pre-checks the two things Graph would reject anyway —
/// a missing <c>usageLocation</c> and a group-assigned licence — so the admin gets a useful sentence instead of a 400.
/// </summary>
public sealed class LicenseService
{
    private readonly GraphClient _graph;
    private readonly CacheDb _db;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;
    private readonly ILogger<LicenseService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Reads taken while the licence change propagates; ~0.4 + 0.8 + 1.6 + 3.2 s of waiting at worst.</summary>
    public const int MaxConvergenceAttempts = 5;

    public LicenseService(GraphClient graph, CacheDb db, ActionLog actions, TenantContext tenant, ILogger<LicenseService> logger, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
        _graph = graph;
        _db = db;
        _actions = actions;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>Every SKU in the tenant, from the cache.</summary>
    public IReadOnlyList<LicenseSku> GetSkus() => _db.GetSkus();

    public LicenseSku? GetSku(string skuId) => _db.GetSkus().FirstOrDefault(s => string.Equals(s.SkuId, skuId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds one SKU. Fails without calling Graph when the user has no usage location (doc 04: required first).
    /// Undo removes it again.
    /// </summary>
    public Task<WriteOutcome> AssignAsync(string userId, string skuId, CancellationToken ct) =>
        ChangeAsync(userId, skuId, assign: true, ct);

    /// <summary>Removes one SKU. Group-assigned licences are refused with the reason (doc 04).</summary>
    public Task<WriteOutcome> RemoveAsync(string userId, string skuId, CancellationToken ct) =>
        ChangeAsync(userId, skuId, assign: false, ct);

    private async Task<WriteOutcome> ChangeAsync(string userId, string skuId, bool assign, CancellationToken ct)
    {
        var user = _db.GetUser(userId);
        var label = user?.DisplayName ?? user?.UserPrincipalName ?? userId;
        var sku = GetSku(skuId);
        var skuName = sku?.DisplayName ?? skuId;
        var verb = assign ? "Added" : "Removed";
        var summary = $"{verb} {skuName} {(assign ? "to" : "from")} {label}";
        var action = assign ? "license.assign" : "license.remove";

        if (assign && string.IsNullOrEmpty(UsageLocation(user)))
        {
            const string reason = "Set a usage location before assigning a license.";
            _actions.Record(NewAction(userId, label, action, $"assignLicense add {skuName}", ActionOutcome.Skipped, reason));
            return new WriteOutcome { Success = false, Summary = summary, FriendlyError = reason };
        }

        var body = assign
            ? new { addLicenses = new[] { new { skuId, disabledPlans = Array.Empty<string>() } }, removeLicenses = Array.Empty<string>() }
            : (object)new { addLicenses = Array.Empty<object>(), removeLicenses = new[] { skuId } };

        try
        {
            await _graph.SendAsync(HttpMethod.Post, $"users/{userId}/assignLicense", body, ct).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            var id = _actions.Record(NewAction(userId, label, action, $"assignLicense {(assign ? "add" : "remove")} {skuName}", ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("{Action} {Sku} on {UserId} failed: {Code}", action, skuId, userId, ex.ErrorCode);
            return WriteOutcome.Failed(summary, ex, id, Capabilities.AssignLicense.RoleHint);
        }

        await WriteThroughAsync(userId, skuId, expectAssigned: assign, ct).ConfigureAwait(false);

        var inverse = JsonSerializer.Serialize(new { skuId, assign = !assign }, GraphJson.Options);
        var actionId = _actions.Record(NewAction(userId, label, action, $"assignLicense {(assign ? "add" : "remove")} {skuName}", ActionOutcome.Success, summary, null, assign ? "license.remove" : "license.assign", inverse));
        _logger.LogInformation("{Action} {Sku} on {UserId}", action, skuId, userId);

        return new WriteOutcome
        {
            Success = true,
            Summary = summary,
            ActionId = actionId,
            Undo = async token =>
            {
                var back = await ChangeAsync(userId, skuId, !assign, token).ConfigureAwait(false);
                if (back.Success) _actions.MarkUndone(actionId);
                return back;
            },
        };
    }

    /// <summary>Doc 04: group-assigned licences cannot be removed per user; detect via <c>licenseAssignmentStates</c>.</summary>
    public static string? RemovalBlockedReason(LicenseAssignmentState? state) =>
        state?.IsGroupAssigned == true ? "Assigned by a group — remove the user from that group instead." : null;

    private static string? UsageLocation(DirectoryUser? user)
    {
        if (user is null) return null;
        using var raw = user.ParseRaw();
        return raw.RootElement.GetString("usageLocation");
    }

    /// <summary>
    /// Unlike a user PATCH, <c>assignLicense</c> is eventually consistent: a read taken immediately after it can still
    /// show the old licence set, which would poison the cache with a value that is about to change. Poll briefly for the
    /// expected state, then write through whatever the last read gave; the delta tick corrects anything still lagging.
    /// </summary>
    private async Task WriteThroughAsync(string userId, string skuId, bool expectAssigned, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(400);
        for (var attempt = 1; attempt <= MaxConvergenceAttempts; attempt++)
        {
            DirectoryUser? user;
            try
            {
                using var doc = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}?$select={GraphSelects.UserDelta}", null, ct).ConfigureAwait(false);
                if (doc is null) return;
                user = DirectoryUser.FromJson(doc.RootElement);
            }
            catch (GraphException ex)
            {
                _logger.LogWarning("Write-through re-read for {UserId} failed: {Code}", userId, ex.ErrorCode);
                return;
            }

            var converged = HasLicense(user, skuId) == expectAssigned;
            if (converged || attempt == MaxConvergenceAttempts)
            {
                _db.Write(w => w.UpsertUser(user));
                if (!converged)
                {
                    _logger.LogInformation("Licence change on {UserId} not visible after {Attempts} reads; the next delta will correct the cache", userId, attempt);
                }

                return;
            }

            await _delay(delay, ct).ConfigureAwait(false);
            delay += delay;
        }
    }

    private static bool HasLicense(DirectoryUser user, string skuId)
    {
        using var raw = user.ParseRaw();
        if (!raw.RootElement.TryGetProperty("assignedLicenses", out var a) || a.ValueKind != JsonValueKind.Array) return false;
        return a.EnumerateArray().Any(l => string.Equals(l.GetString("skuId"), skuId, StringComparison.OrdinalIgnoreCase));
    }

    private ActionRecord NewAction(string userId, string label, string action, string request, ActionOutcome outcome, string? message = null, string? requestId = null, string? inverseAction = null, string? inversePayload = null) => new()
    {
        Actor = _tenant.SignedInUpn,
        TenantId = _tenant.TenantId,
        TargetType = ObjectType.User,
        TargetId = userId,
        TargetName = label,
        Action = action,
        RequestSummary = request,
        Outcome = outcome,
        Message = message,
        GraphRequestId = requestId,
        InverseAction = inverseAction,
        InversePayload = inversePayload,
    };
}
