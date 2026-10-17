using AdminConsole.Core.Auth;
using AdminConsole.Core.OneDrive;
using AdminConsole.Core.SharePoint;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 15, un-greyed in M16: the delegate becomes a site collection admin of the leaver's OneDrive, so the files
/// stay reachable after the licence goes and before the retention period deletes them. Reversible.
/// </summary>
public sealed class DelegateOneDriveStep : IOffboardingStep
{
    private const string Key = "onedrive.detail";

    private readonly OneDriveService _oneDrive;

    public DelegateOneDriveStep(OneDriveService oneDrive) => _oneDrive = oneDrive;

    public string Id => "delegate-onedrive";
    public string Title => "Give the delegate access to OneDrive";
    public StepSurface Surface => StepSurface.SharePoint;
    public Capability? RequiredCapability => Capabilities.ManageOneDrive;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await OneDriveStep.FindAsync(_oneDrive, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;
        var od = found.OneDrive!;
        ctx.Set(Key, od);

        if (ctx.Delegate is not { } d) return StepPreview.Blocked(Id, Title, "Choose a delegate first — access has to go to someone");
        if (d.UserPrincipalName is not { } upn) return StepPreview.Blocked(Id, Title, $"{d.Label} has no sign-in name, so SharePoint can't be told who to add");
        if (od.Admins.Any(a => a.Is(upn))) return StepPreview.NoOp(Id, $"{d.Label} already administers the OneDrive");
        if (od.IsLocked) return StepPreview.Blocked(Id, Title, "The OneDrive is locked, and SharePoint refuses changes to a locked site. Unlock it first.");

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Make {d.Label} an admin of the OneDrive ({od.StorageUsedMb:N0} MB)",
            Details = [od.Url],
            Reason = "The OneDrive is kept for the retention period after the licence goes; this is how the files are reached in that time.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (ctx.Get<OneDriveDetail>(Key) is not { } od || ctx.Delegate is not { UserPrincipalName: { } upn } d) return StepResult.Skipped(Id, "No delegate was chosen");
        return OneDriveStep.Result(Id, await _oneDrive.SetAdminAsync(od, ctx.UserLabel, upn, d.Label, admin: true, ct).ConfigureAwait(false));
    }
}
