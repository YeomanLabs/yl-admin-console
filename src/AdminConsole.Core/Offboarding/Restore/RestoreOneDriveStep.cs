using AdminConsole.Core.Auth;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.OneDrive;
using AdminConsole.Core.SharePoint;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Restore;

/// <summary>
/// The OneDrive as the snapshot found it (M16): unlocked first if it was, then sharing, then admins — admins added since
/// (the delegate, usually) taken off and recorded ones put back. In that order because SharePoint refuses every change to a
/// locked site.
/// </summary>
internal sealed class RestoreOneDriveStep(OneDriveService oneDrive) : RestoreStep("restore-onedrive", "Put back OneDrive access", StepSurface.SharePoint, Capabilities.ManageOneDrive)
{
    private const string PlanKey = "restore.onedrive.plan";

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.OneDrive is not { } recorded) return StepPreview.NoOp(Id, "The snapshot recorded no OneDrive");
        if (!oneDrive.IsAvailable) return StepPreview.Blocked(Id, Title, Steps.OneDriveStep.NotGranted);

        OneDriveDetail? now;
        try
        {
            now = await oneDrive.GetAsync(ctx.UserId, ct).ConfigureAwait(false);
        }
        catch (SharePointException ex)
        {
            return StepPreview.Error(Id, Title, $"The OneDrive could not be read. {SharePointException.Friendly(ex)}");
        }

        if (now is null) return StepPreview.NoOp(Id, "The OneDrive no longer exists — past its retention period, or deleted");

        var label = ctx.UserLabel;
        var plan = new List<Change>();
        if (!string.Equals(now.LockState, recorded.LockState, StringComparison.OrdinalIgnoreCase))
        {
            plan.Add(new Change($"Set the lock back to {OneDriveLock.Describe(recorded.LockState).ToLowerInvariant()}",
                token => oneDrive.SetLockStateAsync(now, label, recorded.LockState, token)));
        }

        if ((int)now.Sharing != recorded.Sharing)
        {
            var sharing = (OneDriveSharing)recorded.Sharing;
            plan.Add(new Change($"Set sharing back to {OneDriveSharingText.Describe(sharing).ToLowerInvariant()}", token => oneDrive.SetSharingAsync(now, label, sharing, token)));
        }

        foreach (var extra in now.Admins.Where(a => a.UserPrincipalName is { } u && !recorded.Admins.Contains(u, StringComparer.OrdinalIgnoreCase)
                                                  && !string.Equals(u, now.Owner, StringComparison.OrdinalIgnoreCase)))
        {
            plan.Add(new Change($"Take {extra.Label} off the admins, added since the snapshot",
                token => oneDrive.SetAdminAsync(now, label, extra.UserPrincipalName!, extra.Label, admin: false, token)));
        }

        foreach (var missing in recorded.Admins.Where(u => u.Contains('@') && !now.Admins.Any(a => a.Is(u))))
        {
            plan.Add(new Change($"Make {missing} an admin again", token => oneDrive.SetAdminAsync(now, label, missing, missing, admin: true, token)));
        }

        ctx.Set(PlanKey, plan);
        return plan.Count == 0
            ? StepPreview.NoOp(Id, "The OneDrive is as the snapshot recorded it")
            : new StepPreview { StepId = Id, Summary = $"Put back {Plural(plan.Count, "OneDrive setting")}", Details = [.. plan.Select(c => c.Label)] };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var writes = new List<(string, WriteOutcome)>();
        foreach (var change in ctx.Get<List<Change>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            writes.Add((change.Label, await change.Apply(ct).ConfigureAwait(false)));
        }

        return Combined(Id, n => $"Put back {Plural(n, "OneDrive setting")}", writes);
    }

    private sealed record Change(string Label, Func<CancellationToken, Task<WriteOutcome>> Apply);
}
