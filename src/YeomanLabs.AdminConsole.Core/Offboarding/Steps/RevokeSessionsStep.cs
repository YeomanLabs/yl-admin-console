using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 2. Disabling the account stops new sign-ins; this is what ends the sessions already open. Not
/// reversible, and there is nothing to reverse: the user simply has to sign in again, which they no longer can.
/// </summary>
public sealed class RevokeSessionsStep : IOffboardingStep
{
    private readonly UserService _users;

    public RevokeSessionsStep(UserService users) => _users = users;

    public string Id => "revoke-sessions";
    public string Title => "Revoke all sessions and refresh tokens";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.RevokeSessions;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        // signInActivity is not projected onto DirectoryUser; it rides along in the raw payload when the tenant has P1.
        using var raw = ctx.User.ParseRaw();
        var last = SignInActivity.FromJson(raw.RootElement)?.LastSeen;
        var when = last is null ? null : $" — last signed in {Relative(last.Value)}";

        return Task.FromResult(new StepPreview
        {
            StepId = Id,
            Summary = $"Revoke every session and refresh token for {ctx.User.UserPrincipalName}{when}",
        });
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var outcome = await _users.RevokeSessionsAsync(ctx.UserId, ct).ConfigureAwait(false);
        return outcome.Success
            ? StepResult.Success(Id, "Sessions and refresh tokens revoked")
            : StepResult.Failed(Id, outcome.FriendlyError ?? outcome.Summary, outcome.Error);
    }

    private static string Relative(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        if (age < TimeSpan.FromMinutes(2)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} minutes ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} hours ago";
        return $"{(int)age.TotalDays} days ago";
    }
}
