using System.Globalization;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Users;

public sealed partial class UserService
{
    /// <summary>
    /// Issues a Temporary Access Pass (M18.1): a time-limited code a person signs in with to register MFA or a passkey,
    /// typically on day one or after losing a phone. The pass itself comes back once, in this result, and goes nowhere
    /// else — not the action log (whose redactor also lists it), not a toast, not the cache.
    /// </summary>
    /// <remarks>
    /// A user can hold one pass at a time, and Graph's refusal of a second is a bare 400, so an existing pass is looked
    /// for first and named. The lifetime limits are the tenant's policy; a lifetime outside them is Graph's to refuse,
    /// and its message (which states the range) is passed through.
    /// </remarks>
    public async Task<TapResult> IssueTemporaryAccessPassAsync(string userId, int lifetimeMinutes, bool usableOnce, DateTimeOffset? start, CancellationToken ct)
    {
        var user = _db.GetUser(userId);
        var label = user?.DisplayName ?? user?.UserPrincipalName ?? userId;
        var summary = $"Issued a Temporary Access Pass to {label}";
        var request = $"POST temporaryAccessPassMethods lifetime={lifetimeMinutes}m once={(usableOnce ? "yes" : "no")}" +
                      (start is { } s ? $" start={s.UtcDateTime:yyyy-MM-ddTHH:mmZ}" : string.Empty);

        try
        {
            using var existing = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}/authentication/temporaryAccessPassMethods", null, ct).ConfigureAwait(false);
            if (existing is not null && existing.RootElement.TryGetProperty("value", out var list) && list.GetArrayLength() > 0)
            {
                const string reason = "This person already has a Temporary Access Pass. Remove it under Authentication methods, then issue a new one.";
                _actions.Record(NewAction(userId, label, "user.auth.tap", request, ActionOutcome.Skipped, reason));
                return new TapResult(new WriteOutcome { Success = false, Summary = summary, FriendlyError = reason }, null, null, null, usableOnce);
            }
        }
        catch (GraphException ex) when (ex.StatusCode == 403)
        {
            // Reading needs the same permission as writing; let the POST give the real answer.
        }

        var body = new Dictionary<string, object?>
        {
            ["lifetimeInMinutes"] = lifetimeMinutes,
            ["isUsableOnce"] = usableOnce,
        };
        if (start is { } at) body["startDateTime"] = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        string? pass;
        DateTimeOffset? from;
        int? lifetime;
        try
        {
            using var doc = await _graph.SendAsync(HttpMethod.Post, $"users/{userId}/authentication/temporaryAccessPassMethods", body, ct).ConfigureAwait(false);
            var root = doc!.RootElement;
            pass = root.TryGetProperty("temporaryAccessPass", out var tp) && tp.ValueKind == System.Text.Json.JsonValueKind.String ? tp.GetString() : null;
            from = root.TryGetProperty("startDateTime", out var sd) && sd.ValueKind == System.Text.Json.JsonValueKind.String && sd.TryGetDateTimeOffset(out var parsed) ? parsed : null;
            lifetime = root.TryGetProperty("lifetimeInMinutes", out var lt) && lt.TryGetInt32(out var minutes) ? minutes : lifetimeMinutes;
        }
        catch (GraphException ex)
        {
            var friendly = ex.Message.Contains("policy", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("lifetime", StringComparison.OrdinalIgnoreCase)
                ? ex.Message
                : GraphErrorText.Friendly(ex);
            var id = _actions.Record(NewAction(userId, label, "user.auth.tap", request, ActionOutcome.Failed, friendly, ex.RequestId));
            _logger.LogWarning("Issuing a Temporary Access Pass to {UserId} failed: {Code}", userId, ex.ErrorCode);
            var failed = WriteOutcome.Failed(summary, ex, id, Capabilities.IssueTap.RoleHint);
            return new TapResult(failed with { FriendlyError = friendly }, null, null, null, usableOnce);
        }

        var actionId = _actions.Record(NewAction(userId, label, "user.auth.tap", request, ActionOutcome.Success,
            $"Temporary Access Pass issued: {lifetime} minutes, {(usableOnce ? "one-time" : "reusable")}"));
        _logger.LogInformation("Issued a Temporary Access Pass to {UserId}", userId);
        return new TapResult(new WriteOutcome { Success = true, Summary = summary, ActionId = actionId }, pass, from, lifetime, usableOnce);
    }
}

/// <summary>The outcome of issuing a pass, and the pass itself — shown once and then dropped.</summary>
public sealed record TapResult(WriteOutcome Outcome, string? Pass, DateTimeOffset? Start, int? LifetimeMinutes, bool UsableOnce)
{
    /// <summary>"Valid from 14:05 for 1 hour · one sign-in".</summary>
    public string Caption
    {
        get
        {
            var when = Start is { } s ? s.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "now";
            var span = LifetimeMinutes switch
            {
                null => "its default lifetime",
                < 60 => $"{LifetimeMinutes} minutes",
                60 => "1 hour",
                var m when m % 1440 == 0 => m == 1440 ? "1 day" : $"{m / 1440} days",
                var m when m % 60 == 0 => $"{m / 60} hours",
                var m => $"{m} minutes",
            };
            return $"Valid from {when} for {span} · {(UsableOnce ? "one sign-in" : "any number of sign-ins")}";
        }
    }
}
