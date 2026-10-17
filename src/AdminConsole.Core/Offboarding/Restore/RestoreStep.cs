using AdminConsole.Core.Auth;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.Users;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Restore;

/// <summary>
/// One step of a restore (M15.4): an offboarding step in reverse. Each compares the snapshot with the account as it is
/// now and puts back only what differs, so a restore of an account that was never fully offboarded is mostly no-ops,
/// and running it twice changes nothing the second time.
/// </summary>
internal abstract class RestoreStep : IOffboardingStep
{
    protected RestoreStep(string id, string title, StepSurface surface, Capability? capability = null)
    {
        Id = id;
        Title = title;
        Surface = surface;
        RequiredCapability = capability;
    }

    public string Id { get; }
    public string Title { get; }
    public StepSurface Surface { get; }
    public Capability? RequiredCapability { get; }
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct) =>
        ctx.RestoreFrom is { } snapshot ? PreviewAsync(ctx, snapshot, ct) : Task.FromResult(StepPreview.Blocked(Id, Title, "No snapshot was chosen."));

    public Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct) =>
        ctx.RestoreFrom is { } snapshot ? ExecuteAsync(ctx, snapshot, ct) : Task.FromResult(StepResult.Skipped(Id, "No snapshot was chosen."));

    protected abstract Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct);

    protected abstract Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct);

    /// <summary>The account as it is now, read once per preview however many steps ask.</summary>
    protected static Task<UserLiveDetail> LiveAsync(UserService users, OffboardingContext ctx, CancellationToken ct) =>
        ctx.GetOrAdd("restore.live", () => users.HydrateAsync(ctx.UserId, ct));

    /// <summary>
    /// The AD account, by the GUID the snapshot recorded rather than by the cloud account's source anchor: the GUID is
    /// what the snapshot is sure of, and it survives a move into an OU that is out of sync scope.
    /// </summary>
    protected static async Task<(OnPremUser? User, string? Blocked)> FindAdAsync(IDirectoryService directory, SnapshotOnPrem recorded, CancellationToken ct)
    {
        DirectoryStatus status;
        try
        {
            status = await directory.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"Active Directory could not be reached. {ex.Message}");
        }

        if (!status.IsConnected) return (null, status.Reason ?? "No domain controller is reachable from here.");
        var user = await directory.GetUserAsync(recorded.ObjectGuid, ct).ConfigureAwait(false);
        return user is null ? (null, $"{recorded.SamAccountName} is no longer in {status.DomainName}.") : (user, null);
    }

    /// <summary>Several writes as one step, undo reversing what landed — the Exchange helper's rule, for any surface.</summary>
    protected static StepResult Combined(string stepId, Func<int, string> describe, IReadOnlyList<(string Label, WriteOutcome Outcome)> writes) =>
        Steps.ExchangeStep.Combined(stepId, describe, "changes", writes);

    protected static string Plural(int n, string noun, string? plural = null) => Steps.ExchangeStep.Plural(n, noun, plural);

    protected static WriteOutcome FromDirectory(DirectoryOutcome o, Func<CancellationToken, Task<WriteOutcome>>? undo = null) => new()
    {
        Success = o.Success,
        Summary = o.Summary,
        FriendlyError = o.FriendlyError,
        WasNoOp = o.WasNoOp,
        Warning = o.Warning,
        Undo = o.Success && !o.WasNoOp ? undo : null,
    };
}
