using AdminConsole.Core.Auth;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 18, no longer greyed (M12.7). Moves the AD object into the OU leavers are kept in.
/// </summary>
/// <remarks>
/// <para>
/// Which OU that is differs per organisation, so it is configuration rather than a guess: <c>OnPrem:DisabledOu</c>.
/// Left unset, the step previews as blocked and names the setting, because moving somebody's account to an OU
/// picked by the console would be a change nobody asked for, applied to the one object it matters most on.
/// </para>
/// <para>
/// It is step 18 and the disable is 19, so the move happens first. That ordering is deliberate: the cloud disable
/// (step 1) has already blocked sign-in by this point, and doing the move before the on-prem disable means the
/// account lands in the leavers OU while it is still a live object — so a mistake is visible and reversible rather
/// than being discovered later against something already switched off.
/// </para>
/// </remarks>
public sealed class MoveToDisabledOuStep : IOffboardingStep
{
    private const string FoundUser = "onprem.user";

    private readonly IDirectoryService _directory;
    private readonly LdapOptions _options;

    public MoveToDisabledOuStep(IDirectoryService directory, LdapOptions options)
    {
        _directory = directory;
        _options = options;
    }

    public string Id => "move-ou";
    public string Title => "Move the AD object to the disabled OU";
    public StepSurface Surface => StepSurface.OnPremAd;
    public Capability? RequiredCapability => null;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (!ctx.User.OnPremisesSyncEnabled)
        {
            return StepPreview.NoOp(Id, $"{ctx.UserLabel} is cloud-only, so there is no AD object to move");
        }

        if (string.IsNullOrWhiteSpace(_options.DisabledOu))
        {
            return StepPreview.Blocked(Id, Title,
                "No leavers OU is configured. Set OnPrem:DisabledOu to its distinguished name — the console will not " +
                "pick one for you.");
        }

        var found = await OnPremStep.FindAsync(_directory, ctx, ct).ConfigureAwait(false);
        if (found.Blocked is { } blocked) return StepPreview.Blocked(Id, Title, blocked);

        var user = found.User!;
        ctx.Set(FoundUser, user);

        var target = _options.DisabledOu.Trim();
        if (string.Equals(DistinguishedNames.Parent(user.DistinguishedName), target, StringComparison.OrdinalIgnoreCase))
        {
            return StepPreview.NoOp(Id, $"{user.SamAccountName} is already in {DistinguishedNames.FriendlyPath(target)}");
        }

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Move {user.SamAccountName} from {DistinguishedNames.Friendly(user.DistinguishedName)} " +
                      $"to {DistinguishedNames.FriendlyPath(target)}",
            Reason = "The object keeps its objectGUID, so the link to the cloud account survives the move.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var user = ctx.Get<OnPremUser>(FoundUser) ?? (await OnPremStep.FindAsync(_directory, ctx, ct).ConfigureAwait(false)).User;
        if (user is null) return StepResult.Failed(Id, "The AD account could not be found.");

        var from = DistinguishedNames.Friendly(user.DistinguishedName);
        var outcome = await _directory.MoveAsync(user.ObjectGuid, _options.DisabledOu.Trim(), ct).ConfigureAwait(false);
        if (!outcome.Success) return StepResult.Failed(Id, outcome.FriendlyError ?? outcome.Summary);

        // Undo puts it back where it came from. A move is one of the few AD writes that is genuinely reversible,
        // and an offboarding run started against the wrong person is exactly when that matters.
        var origin = DistinguishedNames.Parent(user.DistinguishedName);
        return StepResult.Success(Id,
            $"Moved {user.SamAccountName} to {DistinguishedNames.FriendlyPath(_options.DisabledOu.Trim())}",
            origin is null ? null : async token =>
            {
                var back = await _directory.MoveAsync(user.ObjectGuid, origin, token).ConfigureAwait(false);
                return back.Success
                    ? new Writes.WriteOutcome { Success = true, Summary = $"Moved {user.SamAccountName} back to {from}" }
                    : new Writes.WriteOutcome { Success = false, Summary = "Not moved back", FriendlyError = back.FriendlyError };
            });
    }
}
