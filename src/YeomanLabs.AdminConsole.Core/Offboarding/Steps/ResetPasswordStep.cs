using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 3. A 24-character random password nobody ever sees: it is not shown, not logged, and not returned.
/// On a synced tenant without password writeback Graph answers 400, which is a skip with a reason rather than a
/// failure — the account is already disabled by then, so the leaver cannot use the old password either way.
/// </summary>
public sealed class ResetPasswordStep : IOffboardingStep
{
    private const int Length = 24;

    private readonly UserService _users;

    public ResetPasswordStep(UserService users) => _users = users;

    public string Id => "reset-password";
    public string Title => "Reset the password";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.ResetPassword;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct) =>
        Task.FromResult(new StepPreview
        {
            StepId = Id,
            Summary = $"Reset the password for {ctx.User.UserPrincipalName} to {Length} random characters, shown to nobody",
            Reason = ctx.User.OnPremisesSyncEnabled
                ? "Synced account — this needs password writeback. Without it Graph refuses and the step is skipped."
                : null,
        });

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var result = await _users.ResetPasswordAsync(ctx.UserId, UserService.GeneratePassword(Length), forceChangeNextSignIn: true, ct).ConfigureAwait(false);
        if (result.Outcome.Success) return StepResult.Success(Id, "Password reset to a random value");

        // Doc 06: on a synced tenant with no writeback this is an expected refusal, not a broken step.
        if (result.Outcome.Error is { } error && IsWritebackRefusal(error) && ctx.User.OnPremisesSyncEnabled)
        {
            return StepResult.Skipped(Id, "Skipped — this tenant does not have password writeback enabled");
        }

        return StepResult.Failed(Id, result.Outcome.FriendlyError ?? result.Outcome.Summary, result.Outcome.Error);
    }

    private static bool IsWritebackRefusal(GraphException error) =>
        error.StatusCode == 400 || error.Message.Contains("writeback", StringComparison.OrdinalIgnoreCase)
                                || error.Message.Contains("on-premises", StringComparison.OrdinalIgnoreCase);
}
