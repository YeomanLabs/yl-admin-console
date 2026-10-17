using AdminConsole.Core.Auth;
using AdminConsole.Core.OneDrive;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Exports the leaver's OneDrive to a ZIP in the profile's export folder (M16.4). Off by default: it can take as long as
/// the OneDrive is large, and most leavers' files are handed over through step 15 instead.
/// </summary>
public sealed class ExportOneDriveStep : IOffboardingStep
{
    private const string Key = "onedrive.export.detail";

    private readonly OneDriveService _oneDrive;
    private readonly OneDriveExporter _exporter;

    public ExportOneDriveStep(OneDriveService oneDrive, OneDriveExporter exporter)
    {
        _oneDrive = oneDrive;
        _exporter = exporter;
    }

    public string Id => "export-onedrive";
    public string Title => "Export the OneDrive to a ZIP";
    public StepSurface Surface => StepSurface.SharePoint;
    public Capability? RequiredCapability => Capabilities.ExportOneDrive;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    /// <summary>Where the ZIP goes: <c>{folder}\{sign-in name} OneDrive {yyyy-MM-dd}.zip</c>.</summary>
    public static string ZipPathFor(string folder, string upn, DateTimeOffset when) =>
        Path.Combine(folder, $"{string.Join("_", upn.Split(Path.GetInvalidFileNameChars()))} OneDrive {when:yyyy-MM-dd}.zip");

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await OneDriveStep.FindAsync(_oneDrive, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;
        var od = found.OneDrive!;
        if (string.IsNullOrWhiteSpace(ctx.ExportFolder) || !Directory.Exists(ctx.ExportFolder))
        {
            return StepPreview.Blocked(Id, Title, "Set an export folder in the profile (options.exportFolder) that exists on this machine.");
        }

        if (od.LockState == OneDriveLock.NoAccess) return StepPreview.Blocked(Id, Title, "The OneDrive is locked to no access, so nothing can be read from it. Unlock it first.");
        ctx.Set(Key, od);
        var zip = ZipPathFor(ctx.ExportFolder, ctx.User.UserPrincipalName ?? ctx.UserId, DateTimeOffset.Now);
        return new StepPreview
        {
            StepId = Id,
            Summary = $"Export {od.StorageUsedMb:N0} MB to {zip}",
            Reason = od.Admins.Any(a => a.Is(ctx.Tenant.SignedInUpn)) ? null
                : "You'll be made a site admin for the export and taken off again afterwards; both are in the activity log.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (ctx.Get<OneDriveDetail>(Key) is not { } od || ctx.ExportFolder is not { } folder) return StepResult.Skipped(Id, "Nothing to export");
        var zip = ZipPathFor(folder, ctx.User.UserPrincipalName ?? ctx.UserId, DateTimeOffset.Now);
        var result = await _exporter.ExportAsync(od, ctx.UserLabel, zip, null, ct).ConfigureAwait(false);
        return result.Complete ? StepResult.Success(Id, $"{result.Summary} → {zip}") : StepResult.Failed(Id, $"{result.Summary}: {string.Join(", ", result.Failed.Take(5))}");
    }
}
