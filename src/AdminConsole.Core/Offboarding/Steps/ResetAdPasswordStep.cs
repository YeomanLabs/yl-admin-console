using AdminConsole.Core.Auth;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Users;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Resets a synced leaver's password in AD itself (M15.2). The Graph reset (doc 06 step 3) needs password writeback and
/// is skipped without it; this one needs nothing but the admin's own delegation in AD.
/// </summary>
/// <remarks>
/// A fresh random password per account, never shown, never logged, never returned. Some scripted offboardings set every
/// leaver to the same known password so a helpdesk can get back in; that turns every disabled account into one shared
/// secret away from being usable, and the snapshot (M15.1) is what makes getting back in unnecessary.
/// </remarks>
public sealed class ResetAdPasswordStep : IOffboardingStep
{
    private const int Length = 24;
    private const string FoundUser = "onprem.user";

    private readonly IDirectoryService _directory;

    public ResetAdPasswordStep(IDirectoryService directory) => _directory = directory;

    public string Id => "reset-ad-password";
    public string Title => "Reset the AD password to a random one";
    public StepSurface Surface => StepSurface.OnPremAd;
    public Capability? RequiredCapability => null;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (!ctx.User.OnPremisesSyncEnabled)
        {
            return StepPreview.NoOp(Id, $"{ctx.UserLabel} is cloud-only, so there is no AD password to reset");
        }

        var found = await OnPremStep.FindAsync(_directory, ctx, ct).ConfigureAwait(false);
        if (found.Blocked is { } blocked) return StepPreview.Blocked(Id, Title, blocked);
        ctx.Set(FoundUser, found.User!);

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Reset {found.User!.SamAccountName}'s AD password to {Length} random characters, shown to nobody",
            Reason = "Different for every account. A shared \"leaver password\" would make every disabled account one secret away from use.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var user = ctx.Get<OnPremUser>(FoundUser) ?? (await OnPremStep.FindAsync(_directory, ctx, ct).ConfigureAwait(false)).User;
        if (user is null) return StepResult.Failed(Id, "The AD account could not be found.");

        var outcome = await _directory.SetPasswordAsync(user.ObjectGuid, UserService.GeneratePassword(Length), mustChangeAtNextLogon: true, ct).ConfigureAwait(false);
        return outcome.Success
            ? StepResult.Success(Id, $"Reset {user.SamAccountName}'s AD password to a random value")
            : StepResult.Failed(Id, outcome.FriendlyError ?? outcome.Summary);
    }
}
