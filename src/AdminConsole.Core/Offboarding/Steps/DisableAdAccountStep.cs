using AdminConsole.Core.Auth;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 19, no longer greyed (M12.7). Disables the on-prem account, which for a synced user is the only
/// disable that holds.
/// </summary>
/// <remarks>
/// Step 1 disables sign-in through Graph, and on a synced account Entra Connect puts it back at the next sync —
/// which the preview there has always said. This is the step that makes it stick, and until now the dialog listed it
/// greyed with "arrives in Phase 2". An offboarding that reported success while the account came back an hour later
/// is the worst failure this product could have, so the two steps are deliberately both present: the cloud one for
/// speed, this one for permanence.
/// </remarks>
public sealed class DisableAdAccountStep : IOffboardingStep
{
    private const string FoundUser = "onprem.user";

    private readonly IDirectoryService _directory;

    public DisableAdAccountStep(IDirectoryService directory) => _directory = directory;

    public string Id => "disable-ad-account";
    public string Title => "Disable the on-prem AD account";
    public StepSurface Surface => StepSurface.OnPremAd;

    /// <summary>Nothing from Graph. AD enforces what this admin has been delegated, which is the whole point.</summary>
    public Capability? RequiredCapability => null;

    public bool DefaultEnabled => true;

    /// <summary>
    /// The run continues if this fails. It is the most important step for a synced user, but stopping here would
    /// leave licences assigned and sessions live — worse than carrying on and reporting the failure loudly.
    /// </summary>
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (!ctx.User.OnPremisesSyncEnabled)
        {
            return StepPreview.NoOp(Id, $"{ctx.UserLabel} is cloud-only, so there is no AD account to disable");
        }

        var found = await OnPremStep.FindAsync(_directory, ctx, ct).ConfigureAwait(false);
        if (found.Blocked is { } blocked) return StepPreview.Blocked(Id, Title, blocked);

        var user = found.User!;
        ctx.Set(FoundUser, user);

        if (!user.Enabled)
        {
            return StepPreview.NoOp(Id, $"{user.SamAccountName} is already disabled in Active Directory");
        }

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Disable {user.SamAccountName} in Active Directory ({DistinguishedNames.Friendly(user.DistinguishedName)})",
            Reason = "This is the disable that survives the next Entra Connect sync.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        // The preview found it; re-finding would be a second round trip for the same answer.
        var user = ctx.Get<OnPremUser>(FoundUser) ?? (await OnPremStep.FindAsync(_directory, ctx, ct).ConfigureAwait(false)).User;
        if (user is null) return StepResult.Failed(Id, "The AD account could not be found.");

        var outcome = await _directory.SetEnabledAsync(user.ObjectGuid, false, ct).ConfigureAwait(false);
        if (!outcome.Success) return StepResult.Failed(Id, outcome.FriendlyError ?? outcome.Summary);

        return StepResult.Success(Id, $"Disabled {user.SamAccountName} in Active Directory");
    }
}
