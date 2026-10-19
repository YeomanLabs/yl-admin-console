using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Users;

/// <summary>
/// The three security actions on User 360 (UX §4 header, doc 04 §Users): remove an authentication method, revoke
/// sessions, reset the password. None is reversible, so each needs a confirm in the UI. Passwords are never logged.
/// </summary>
public sealed partial class UserService
{
    /// <summary>
    /// Deletes one authentication method. The password method cannot be deleted (doc 04), so it is refused locally.
    /// Not reversible: the user must re-register the method.
    /// </summary>
    /// <summary>A person's registered methods, read live: a reset has to remove what is there now, not what the page read.</summary>
    public async Task<IReadOnlyList<AuthMethod>> ReadAuthMethodsAsync(string userId, CancellationToken ct)
    {
        var page = await _graph.GetPageAsync($"users/{userId}/authentication/methods", ct).ConfigureAwait(false);
        return page.Items.Select(AuthMethod.FromJson).ToList();
    }

    public async Task<WriteOutcome> DeleteAuthMethodAsync(string userId, AuthMethod method, CancellationToken ct)
    {
        var user = _db.GetUser(userId);
        var label = user?.DisplayName ?? user?.UserPrincipalName ?? userId;
        var summary = $"Removed {method.KindLabel} from {label}";

        if (method.DeleteCollectionName is not { } collection)
        {
            var reason = method.Kind == AuthMethodKind.Password
                ? "The password method can't be removed. Reset the password instead."
                : $"{method.KindLabel} can't be removed through Graph.";
            _actions.Record(NewAction(userId, label, "user.auth.delete", $"DELETE {method.Kind}", ActionOutcome.Skipped, reason));
            return new WriteOutcome { Success = false, Summary = summary, FriendlyError = reason };
        }

        try
        {
            await _graph.SendAsync(HttpMethod.Delete, $"users/{userId}/authentication/{collection}/{method.Id}", null, ct).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            var id = _actions.Record(NewAction(userId, label, "user.auth.delete", $"DELETE {collection}/{method.Id}", ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("Delete auth method on {UserId} failed: {Code}", userId, ex.ErrorCode);
            return WriteOutcome.Failed(summary, ex, id, Capabilities.DeleteAuthMethod.RoleHint);
        }

        var actionId = _actions.Record(NewAction(userId, label, "user.auth.delete", $"DELETE {collection}/{method.Id}", ActionOutcome.Success, $"{method.KindLabel} removed"));
        _logger.LogInformation("Removed {Kind} auth method from {UserId}", method.Kind, userId);
        return new WriteOutcome { Success = true, Summary = summary, ActionId = actionId };
    }

    /// <summary>
    /// Invalidates every refresh token and session cookie (doc 04 <c>revokeSignInSessions</c>). Not reversible.
    /// Revoking your own sessions signs you out of this app too, so callers warn first.
    /// </summary>
    public async Task<WriteOutcome> RevokeSessionsAsync(string userId, CancellationToken ct)
    {
        var user = _db.GetUser(userId);
        var label = user?.DisplayName ?? user?.UserPrincipalName ?? userId;
        var summary = $"Revoked all sessions for {label}";

        try
        {
            // Unlike the PATCH and DELETE calls here, this one answers 200 with a body, so the document needs disposing.
            using var doc = await _graph.SendAsync(HttpMethod.Post, $"users/{userId}/revokeSignInSessions", null, ct).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            var id = _actions.Record(NewAction(userId, label, "user.revokeSessions", "POST revokeSignInSessions", ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("Revoke sessions on {UserId} failed: {Code}", userId, ex.ErrorCode);
            return WriteOutcome.Failed(summary, ex, id, Capabilities.RevokeSessions.RoleHint);
        }

        var actionId = _actions.Record(NewAction(userId, label, "user.revokeSessions", "POST revokeSignInSessions", ActionOutcome.Success, summary));
        _logger.LogInformation("Revoked sessions for {UserId}", userId);
        return new WriteOutcome { Success = true, Summary = summary, ActionId = actionId };
    }

    /// <summary>
    /// Sets a new password (doc 04 <c>passwordProfile</c>). The password is returned to the caller for the show-once
    /// dialog and is never written to the action log, the app log, or the cache. Not reversible.
    /// </summary>
    public async Task<PasswordResetResult> ResetPasswordAsync(string userId, string password, bool forceChangeNextSignIn, CancellationToken ct)
    {
        var user = _db.GetUser(userId);
        var label = user?.DisplayName ?? user?.UserPrincipalName ?? userId;
        var summary = $"Reset the password for {label}";
        var request = $"PATCH passwordProfile forceChange={forceChangeNextSignIn}";

        var body = new Dictionary<string, object?>
        {
            ["passwordProfile"] = new Dictionary<string, object?>
            {
                ["password"] = password,
                ["forceChangePasswordNextSignIn"] = forceChangeNextSignIn,
            },
        };

        try
        {
            await _graph.SendAsync(HttpMethod.Patch, $"users/{userId}", body, ct).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            var id = _actions.Record(NewAction(userId, label, "user.password.reset", request, ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("Password reset on {UserId} failed: {Code}", userId, ex.ErrorCode);
            return new PasswordResetResult(WriteOutcome.Failed(summary, ex, id, Capabilities.ResetPassword.RoleHint), null);
        }

        var actionId = _actions.Record(NewAction(userId, label, "user.password.reset", request, ActionOutcome.Success, $"Password reset; force change = {(forceChangeNextSignIn ? "yes" : "no")}"));
        _logger.LogInformation("Reset the password for {UserId}", userId);
        return new PasswordResetResult(new WriteOutcome { Success = true, Summary = summary, ActionId = actionId }, password);
    }

    /// <summary>
    /// A 20-character password from the cryptographic RNG with at least one of each class, shuffled so the classes are
    /// not positional. Ambiguous characters are excluded because these get read aloud and retyped.
    /// </summary>
    public static string GeneratePassword(int length = 20)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+?";
        if (length < 8) throw new ArgumentOutOfRangeException(nameof(length), "A generated password must be at least 8 characters.");

        var all = upper + lower + digits + symbols;
        var chars = new char[length];
        chars[0] = Pick(upper);
        chars[1] = Pick(lower);
        chars[2] = Pick(digits);
        chars[3] = Pick(symbols);
        for (var i = 4; i < length; i++) chars[i] = Pick(all);

        // Fisher-Yates with the crypto RNG, so the guaranteed classes are not always in the first four positions.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);

        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }
}

/// <summary>
/// The outcome of a password reset plus the password itself, for the show-once dialog. The password lives only in memory
/// and is deliberately absent from <see cref="WriteOutcome"/> so it cannot reach a toast or a log by accident.
/// </summary>
public sealed record PasswordResetResult(WriteOutcome Outcome, string? Password);
