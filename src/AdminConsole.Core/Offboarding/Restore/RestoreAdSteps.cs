using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Restore;

/// <summary>Back to the OU the snapshot found the account in, or one the admin chose. Runs before the enable.</summary>
/// <remarks>
/// Moved first because the disabled OU often sits outside Entra Connect's scope or under a restrictive GPO; enabling an
/// account there would bring it back to life in the wrong place.
/// </remarks>
internal sealed class RestoreAdOuStep(IDirectoryService directory) : RestoreStep("restore-ad-ou", "Move the AD account back", StepSurface.OnPremAd)
{
    private const string FoundKey = "restore.ad.user";

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.OnPrem is not { } recorded) return StepPreview.NoOp(Id, "The snapshot has no AD account");
        var (user, blocked) = await FindAdAsync(directory, recorded, ct).ConfigureAwait(false);
        if (blocked is not null) return StepPreview.Blocked(Id, Title, blocked);
        ctx.Set(FoundKey, user!);

        var target = ctx.RestoreOuDn ?? recorded.ParentDn;
        var now = DistinguishedNames.Parent(user!.DistinguishedName);
        if (string.Equals(now, target, StringComparison.OrdinalIgnoreCase)) return StepPreview.NoOp(Id, $"{user.SamAccountName} is already in {DistinguishedNames.FriendlyPath(target)}");

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Move {user.SamAccountName} from {DistinguishedNames.FriendlyPath(now ?? user.DistinguishedName)} to {DistinguishedNames.FriendlyPath(target)}",
            Reason = string.Equals(target, recorded.ParentDn, StringComparison.OrdinalIgnoreCase) ? "Where the snapshot found it." : "The OU you chose, not the one the snapshot recorded.",
        };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var user = ctx.Get<OnPremUser>(FoundKey)!;
        var from = DistinguishedNames.Parent(user.DistinguishedName)!;
        var target = ctx.RestoreOuDn ?? snapshot.OnPrem!.ParentDn;
        var outcome = await directory.MoveAsync(user.ObjectGuid, target, ct).ConfigureAwait(false);
        var write = FromDirectory(outcome, async token => FromDirectory(await directory.MoveAsync(user.ObjectGuid, from, token).ConfigureAwait(false)));
        return write.Success ? StepResult.Success(Id, outcome.Summary, write.Undo) : StepResult.Failed(Id, outcome.FriendlyError ?? outcome.Summary);
    }
}

/// <summary>Re-enables the AD account when the snapshot found it enabled — the enable that survives the next sync.</summary>
internal sealed class RestoreAdAccountStep(IDirectoryService directory) : RestoreStep("restore-ad-account", "Enable the AD account", StepSurface.OnPremAd)
{
    private const string FoundKey = "restore.ad.enable";

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.OnPrem is not { } recorded) return StepPreview.NoOp(Id, "The snapshot has no AD account");
        if (!recorded.Enabled) return StepPreview.NoOp(Id, $"{recorded.SamAccountName} was already disabled when the snapshot was taken");
        var (user, blocked) = await FindAdAsync(directory, recorded, ct).ConfigureAwait(false);
        if (blocked is not null) return StepPreview.Blocked(Id, Title, blocked);
        if (user!.Enabled) return StepPreview.NoOp(Id, $"{user.SamAccountName} is enabled");
        ctx.Set(FoundKey, user);
        return new StepPreview { StepId = Id, Summary = $"Enable {user.SamAccountName} in Active Directory", Reason = "The password stays the random one: set a new one, or issue a Temporary Access Pass." };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var user = ctx.Get<OnPremUser>(FoundKey)!;
        var outcome = await directory.SetEnabledAsync(user.ObjectGuid, true, ct).ConfigureAwait(false);
        var write = FromDirectory(outcome, async token => FromDirectory(await directory.SetEnabledAsync(user.ObjectGuid, false, token).ConfigureAwait(false)));
        return write.Success ? StepResult.Success(Id, $"Enabled {user.SamAccountName} in Active Directory", write.Undo) : StepResult.Failed(Id, outcome.FriendlyError ?? outcome.Summary);
    }
}

/// <summary>Puts the account back in the AD groups the snapshot recorded and it has since left.</summary>
internal sealed class RestoreAdGroupsStep(IDirectoryService directory) : RestoreStep("restore-ad-groups", "Put back the AD groups", StepSurface.OnPremAd)
{
    private const string PlanKey = "restore.ad.groups";
    private const int GroupLookupLimit = 2000;

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.OnPrem is not { } recorded) return StepPreview.NoOp(Id, "The snapshot has no AD account");
        if (recorded.GroupDns.Count == 0) return StepPreview.NoOp(Id, "The snapshot recorded no AD groups beyond the primary group");
        var (user, blocked) = await FindAdAsync(directory, recorded, ct).ConfigureAwait(false);
        if (blocked is not null) return StepPreview.Blocked(Id, Title, blocked);

        var missing = recorded.GroupDns.Where(dn => !user!.MemberOfDns.Contains(dn, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0) return StepPreview.NoOp(Id, $"{user!.SamAccountName} is in every AD group the snapshot recorded");

        var groups = await directory.SearchGroupsAsync(string.Empty, GroupLookupLimit, null, ct).ConfigureAwait(false);
        var byDn = groups.ToDictionary(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase);
        var plan = missing.Where(byDn.ContainsKey).Select(dn => byDn[dn]).ToList();
        var gone = missing.Where(dn => !byDn.ContainsKey(dn)).Select(dn => $"{DistinguishedNames.FriendlyPath(dn)} — no longer exists, or not in the first {GroupLookupLimit} groups").ToList();
        ctx.Set(PlanKey, new Plan(user!, plan));

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Add {user!.SamAccountName} back to {Plural(plan.Count, "AD group")}",
            Details = [.. plan.Select(g => g.Primary), .. gone],
        };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (ctx.Get<Plan>(PlanKey) is not { } plan) return StepResult.Skipped(Id, "Nothing to put back");
        var (user, groups) = plan;
        var writes = new List<(string, WriteOutcome)>();
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await directory.AddToGroupAsync(g.ObjectGuid, user.ObjectGuid, ct).ConfigureAwait(false);
            writes.Add((g.Primary, FromDirectory(outcome, async token => FromDirectory(await directory.RemoveFromGroupAsync(g.ObjectGuid, user.ObjectGuid, token).ConfigureAwait(false)))));
        }

        return Combined(Id, n => $"Added back to {Plural(n, "AD group")}", writes);
    }

    private sealed record Plan(OnPremUser User, List<OnPremGroup> Groups);
}
