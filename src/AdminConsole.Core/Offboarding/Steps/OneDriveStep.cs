using AdminConsole.Core.OneDrive;
using AdminConsole.Core.SharePoint;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>What the OneDrive steps (M16) share: the permission, the OneDrive read once per preview, and "none yet".</summary>
internal static class OneDriveStep
{
    private const string Key = "onedrive.read";

    internal const string NotGranted =
        "Needs the SharePoint permission, which this installation hasn't been granted. Grant it from the user's OneDrive tab, then preview again.";

    internal readonly record struct Found(OneDriveDetail? OneDrive, StepPreview? Stop);

    internal static async Task<Found> FindAsync(OneDriveService oneDrive, OffboardingContext ctx, string stepId, string title, CancellationToken ct)
    {
        if (!oneDrive.IsAvailable) return new Found(null, StepPreview.Blocked(stepId, title, NotGranted));
        try
        {
            var od = await ctx.GetOrAdd(Key, () => oneDrive.GetAsync(ctx.UserId, ct)).ConfigureAwait(false);
            return od is null
                ? new Found(null, StepPreview.NoOp(stepId, $"{ctx.UserLabel} has no OneDrive — SharePoint makes one the first time it is opened"))
                : new Found(od, null);
        }
        catch (SharePointException ex)
        {
            return new Found(null, StepPreview.Error(stepId, title, $"The OneDrive could not be read. {SharePointException.Friendly(ex)}"));
        }
    }

    internal static StepResult Result(string stepId, WriteOutcome outcome) => outcome.Success
        ? StepResult.Success(stepId, outcome.Warning is { } w ? $"{outcome.Summary}. {w}" : outcome.Summary, outcome.Undo)
        : StepResult.Failed(stepId, outcome.FriendlyError ?? outcome.Summary) with { SharePointError = outcome.SharePointError };
}
