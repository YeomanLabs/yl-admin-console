using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 1. The first thing that must work: if sign-in cannot be blocked, nothing later matters, so this step
/// halts the run when it fails. On a synced account Graph accepts the write but Entra Connect reverts it, and the
/// preview says so rather than letting the admin believe the account is shut.
/// </summary>
public sealed class DisableAccountStep : IOffboardingStep
{
    private readonly UserService _users;

    public DisableAccountStep(UserService users) => _users = users;

    public string Id => "disable-account";
    public string Title => "Disable sign-in";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.EditUser;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => true;

    public Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (!ctx.User.AccountEnabled)
        {
            return Task.FromResult(StepPreview.NoOp(Id, $"Sign-in is already disabled for {ctx.User.UserPrincipalName}"));
        }

        return Task.FromResult(new StepPreview
        {
            StepId = Id,
            Summary = $"Disable sign-in for {ctx.User.UserPrincipalName} (currently enabled)",
            Reason = ctx.User.WriteWarning("accountEnabled"),
        });
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var outcome = await _users.UpdateAsync(ctx.UserId, "accountEnabled", false, ct).ConfigureAwait(false);
        if (!outcome.Success) return StepResult.Failed(Id, outcome.FriendlyError ?? outcome.Summary, outcome.Error);

        var message = ctx.User.OnPremisesSyncEnabled
            ? $"Sign-in disabled · {DirectoryUser.SyncedDisableWarning}"
            : "Sign-in disabled";

        return StepResult.Success(Id, message, outcome.Undo);
    }
}
