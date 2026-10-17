using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding.Snapshot;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// The first step of every offboarding (M15.1): record what the account has before anything changes, so a rehire or a
/// run against the wrong person can be put back. It halts the run: if the before-picture cannot be taken or written,
/// nothing else runs, because an offboarding that cannot be reversed must not be the default.
/// </summary>
public sealed class SnapshotStep : IOffboardingStep
{
    public const string StepId = "snapshot";
    private const string Taken = "snapshot.taken";

    private readonly SnapshotService _snapshots;
    private readonly SnapshotStore _store;
    private readonly ActionLog _actions;

    public SnapshotStep(SnapshotService snapshots, SnapshotStore store, ActionLog actions)
    {
        _snapshots = snapshots;
        _store = store;
        _actions = actions;
    }

    public string Id => StepId;
    public string Title => "Record what the account has, before anything changes";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => null;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => true;
    public bool IsRequired => true;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        OffboardingSnapshot s;
        try
        {
            s = await _snapshots.TakeAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            return StepPreview.Error(Id, Title, $"The account's groups and licences could not be read, so there would be nothing to restore from. {GraphErrorText.Friendly(ex)}");
        }

        ctx.Set(Taken, s);
        var parts = new List<string>
        {
            Count(s.Groups.Count, "group"),
            Count(s.Licences.Count, "direct licence"),
        };
        if (s.Manager is not null) parts.Add("the manager");
        if (s.OnPrem is not null) parts.Add($"the AD account in {DistinguishedNamesFriendly(s.OnPrem.ParentDn)}");
        if (s.Mailbox is not null) parts.Add("the mailbox's settings and delegation");

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Record {string.Join(", ", parts)} to a snapshot — what Restore uses to put the account back",
            Reason = s.Gaps.Count == 0 ? null : "Not recorded: " + string.Join(" ", s.Gaps),
            Details = s.Gaps,
        };
    }

    public Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        // The preview's read is seconds old and is exactly what the admin saw; taking another would record a picture
        // nobody reviewed.
        var snapshot = ctx.Get<OffboardingSnapshot>(Taken);
        if (snapshot is null) return Task.FromResult(StepResult.Failed(Id, "No snapshot was taken, so the run cannot safely continue."));

        string path;
        try
        {
            path = _store.Save(snapshot with { TakenAt = DateTimeOffset.UtcNow });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(StepResult.Failed(Id, $"The snapshot could not be written ({ex.Message}), so nothing else will run."));
        }

        _actions.Record(new ActionRecord
        {
            Actor = ctx.Tenant.SignedInUpn,
            TenantId = ctx.Tenant.TenantId,
            TargetType = ObjectType.User,
            TargetId = ctx.UserId,
            TargetName = ctx.UserLabel,
            Action = "offboarding.snapshot",
            RequestSummary = path,
            Outcome = ActionOutcome.Success,
            Message = $"Snapshot saved: {snapshot.Groups.Count} groups, {snapshot.Licences.Count} licences" + (snapshot.Gaps.Count > 0 ? $"; {snapshot.Gaps.Count} section(s) not recorded" : string.Empty),
        });
        ctx.Set("snapshot.path", path);
        return Task.FromResult(StepResult.Success(Id, $"Snapshot saved — {Path.GetFileName(path)}"));
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string DistinguishedNamesFriendly(string dn) => OnPrem.DistinguishedNames.FriendlyPath(dn);
}
