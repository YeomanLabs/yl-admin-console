using System.Text.Json;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Writes;

namespace YeomanLabs.AdminConsole.Core.Users;

/// <summary>
/// User writes (doc 04 §Users). Every write is guarded by the hybrid rule, sent as a minimal PATCH, written through to
/// the cache so the UI is current before the next delta, logged to <c>actions.db</c>, and reversible where the spec says so.
/// </summary>
public sealed partial class UserService
{
    /// <summary>
    /// PATCHes one property. Refuses on-prem-mastered properties (doc 05) without calling Graph, no-ops when the value
    /// is already correct, and returns an <see cref="WriteOutcome.Undo"/> that restores the previous value.
    /// </summary>
    public async Task<WriteOutcome> UpdateAsync(string userId, string propertyName, object? value, CancellationToken ct)
    {
        var user = _db.GetUser(userId);
        var label = user?.DisplayName ?? user?.UserPrincipalName ?? userId;
        var friendly = FriendlyProperty(propertyName);
        var summary = $"Set {friendly} on {label}";

        // Hybrid guard (CLAUDE.md non-negotiable 6): the UI greys these, and the service still refuses.
        if (user is not null && user.IsReadOnly(propertyName))
        {
            var reason = DirectoryUser.HybridReadOnlyReason;
            _actions.Record(NewAction(userId, label, $"user.patch.{propertyName}", $"PATCH {propertyName}", ActionOutcome.Skipped, reason));
            _logger.LogInformation("Refused {Property} on {UserId}: mastered on-prem", propertyName, userId);
            return new WriteOutcome { Success = false, Summary = summary, FriendlyError = reason };
        }

        var previous = user is null ? null : ReadProperty(user, propertyName);
        if (JsonEquals(previous, value))
        {
            return WriteOutcome.NoOp($"{friendly} is already set to that value");
        }

        var body = new Dictionary<string, object?> { [propertyName] = value };

        // The request summary is free text, so ActionLog.Redact (which only walks JSON) cannot clean it. Never put a
        // secret-valued property in it.
        var request = IsSecret(propertyName) ? $"PATCH {propertyName}=***" : $"PATCH {propertyName}={Describe(value)}";

        try
        {
            await _graph.SendAsync(HttpMethod.Patch, $"users/{userId}", body, ct).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            var id = _actions.Record(NewAction(userId, label, $"user.patch.{propertyName}", request, ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("PATCH {Property} on {UserId} failed: {Code}", propertyName, userId, ex.ErrorCode);
            return WriteOutcome.Failed(summary, ex, id, Capabilities.EditUser.RoleHint);
        }

        await WriteThroughAsync(userId, ct).ConfigureAwait(false);

        var inverse = JsonSerializer.Serialize(new Dictionary<string, object?> { [propertyName] = previous }, GraphJson.Options);
        var message = IsSecret(propertyName) ? $"{friendly} changed" : $"{friendly} → {Describe(value)}";
        var actionId = _actions.Record(NewAction(userId, label, $"user.patch.{propertyName}", request, ActionOutcome.Success, message, null, "user.patch", inverse));
        _logger.LogInformation("Patched {Property} on {UserId}", propertyName, userId);

        return new WriteOutcome
        {
            Success = true,
            Summary = IsSecret(propertyName) ? summary : $"{summary} · was {Describe(previous)}",
            ActionId = actionId,
            Undo = async token =>
            {
                var back = await UpdateAsync(userId, propertyName, previous, token).ConfigureAwait(false);
                if (back.Success) _actions.MarkUndone(actionId);
                return back with { Summary = $"Restored {friendly} on {label}" };
            },
        };
    }

    /// <summary>Re-reads the user and replaces the cached projection, so the UI is correct before the next delta tick.</summary>
    private async Task WriteThroughAsync(string userId, CancellationToken ct)
    {
        try
        {
            using var doc = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}?$select={GraphSelects.UserDelta}", null, ct).ConfigureAwait(false);
            if (doc is not null) _db.Write(w => w.UpsertUser(DirectoryUser.FromJson(doc.RootElement)));
        }
        catch (GraphException ex)
        {
            // The write itself succeeded; a stale cache row is corrected by the next delta.
            _logger.LogWarning("Write-through re-read for {UserId} failed: {Code}", userId, ex.ErrorCode);
        }
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

    /// <summary>The current value of a projected or raw property, as a JSON-shaped object suitable for a PATCH.</summary>
    private static object? ReadProperty(DirectoryUser user, string propertyName)
    {
        using var doc = user.ParseRaw();
        if (!doc.RootElement.TryGetProperty(propertyName, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
            JsonValueKind.Array => v.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : i.GetRawText()).ToArray(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        };
    }

    private static bool JsonEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return IsEmpty(a) && IsEmpty(b);
        return JsonSerializer.Serialize(a, GraphJson.Options) == JsonSerializer.Serialize(b, GraphJson.Options);
    }

    private static bool IsEmpty(object? v) => v switch
    {
        null => true,
        string s => s.Length == 0,
        System.Collections.ICollection c => c.Count == 0,
        _ => false,
    };

    /// <summary>Properties whose value must never be written to a log line, even in passing.</summary>
    private static readonly HashSet<string> SecretProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "passwordProfile", "password", "temporaryAccessPass", "secretKey",
    };

    internal static bool IsSecret(string propertyName) => SecretProperties.Contains(propertyName);

    internal static string Describe(object? value) => value switch
    {
        null => "empty",
        string s when s.Length == 0 => "empty",
        string s => $"\"{s}\"",
        bool b => b ? "yes" : "no",
        string?[] a => a.Length == 0 ? "empty" : $"\"{string.Join(", ", a)}\"",
        System.Collections.IEnumerable e => $"\"{string.Join(", ", e.Cast<object?>())}\"",
        _ => value.ToString() ?? "empty",
    };

    /// <summary>Graph property name → the words the toast and the action log use.</summary>
    public static string FriendlyProperty(string propertyName) => propertyName switch
    {
        "displayName" => "display name",
        "givenName" => "first name",
        "surname" => "last name",
        "jobTitle" => "job title",
        "department" => "department",
        "companyName" => "company",
        "officeLocation" => "office",
        "employeeId" => "employee ID",
        "employeeType" => "employee type",
        "businessPhones" => "business phones",
        "mobilePhone" => "mobile phone",
        "streetAddress" => "street",
        "city" => "city",
        "state" => "state",
        "postalCode" => "postal code",
        "country" => "country",
        "usageLocation" => "usage location",
        "preferredLanguage" => "preferred language",
        "accountEnabled" => "sign-in",
        _ => propertyName,
    };
}
