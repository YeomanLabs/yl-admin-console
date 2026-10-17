using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Groups;
using AdminConsole.Core.Licensing;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.Users;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Restore;

/// <summary>Turns sign-in back on. For a synced account the AD enable is the one that holds; this one is immediate.</summary>
internal sealed class RestoreSignInStep(UserService users) : RestoreStep("restore-sign-in", "Enable sign-in", StepSurface.Graph, Capabilities.EditUser)
{
    protected override Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (!snapshot.AccountEnabled) return Task.FromResult(StepPreview.NoOp(Id, "Sign-in was already disabled when the snapshot was taken"));
        if (ctx.User.AccountEnabled) return Task.FromResult(StepPreview.NoOp(Id, $"Sign-in is enabled for {ctx.User.UserPrincipalName}"));
        return Task.FromResult(new StepPreview
        {
            StepId = Id,
            Summary = $"Enable sign-in for {ctx.User.UserPrincipalName}",
            Reason = ctx.User.OnPremisesSyncEnabled ? "Synced: the AD enable above is what survives the next sync; this makes it immediate." : null,
        });
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var outcome = await users.UpdateAsync(ctx.UserId, "accountEnabled", true, ct).ConfigureAwait(false);
        return outcome.Success ? StepResult.Success(Id, "Sign-in enabled", outcome.Undo) : StepResult.Failed(Id, outcome.FriendlyError ?? outcome.Summary, outcome.Error);
    }
}

/// <summary>Re-assigns the direct licences the snapshot recorded. Group-assigned ones come back with their groups.</summary>
internal sealed class RestoreLicencesStep(UserService users, LicenseService licenses) : RestoreStep("restore-licences", "Assign the licences again", StepSurface.Graph, Capabilities.AssignLicense)
{
    private const string PlanKey = "restore.licences";

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Licences.Count == 0) return StepPreview.NoOp(Id, "The snapshot recorded no direct licences");
        var live = await LiveAsync(users, ctx, ct).ConfigureAwait(false);
        if (live.LicenseStates.Error is { } error) return StepPreview.Error(Id, Title, GraphErrorText.Friendly(error));

        var held = (live.LicenseStates.Value ?? []).Select(l => l.SkuId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = snapshot.Licences.Where(l => !held.Contains(l.Id)).ToList();
        ctx.Set(PlanKey, missing);
        if (missing.Count == 0) return StepPreview.NoOp(Id, "Every licence the snapshot recorded is assigned");

        var short_ = missing.Where(l => licenses.GetSku(l.Id) is { } sku && sku.Available <= 0).Select(l => Name(l)).ToList();
        return new StepPreview
        {
            StepId = Id,
            Summary = $"Assign {Plural(missing.Count, "licence")}: {string.Join(", ", missing.Select(Name))}",
            Reason = short_.Count > 0 ? $"No seats free for {string.Join(", ", short_)} — that assignment will fail until one is." : null,
        };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var writes = new List<(string, WriteOutcome)>();
        foreach (var l in ctx.Get<List<SnapshotRef>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            writes.Add((Name(l), await licenses.AssignAsync(ctx.UserId, l.Id, ct).ConfigureAwait(false)));
        }

        return Combined(Id, n => $"Assigned {Plural(n, "licence")}", writes);
    }

    private string Name(SnapshotRef l) => licenses.GetSku(l.Id)?.DisplayName ?? l.Name ?? l.Id;
}

/// <summary>Adds the account back to the cloud groups it left. Synced groups return with AD; lists with Exchange.</summary>
internal sealed class RestoreGroupsStep(UserService users, GroupService groups) : RestoreStep("restore-groups", "Put back the cloud groups", StepSurface.Graph, Capabilities.ChangeGroupMembership)
{
    private const string PlanKey = "restore.groups";

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var live = await LiveAsync(users, ctx, ct).ConfigureAwait(false);
        if (live.Groups.Error is { } error) return StepPreview.Error(Id, Title, GraphErrorText.Friendly(error));

        var now = (live.Groups.Value ?? []).Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cloud = snapshot.Groups.Where(g => !g.Synced && !g.Dynamic && !g.MailEnabledList && !now.Contains(g.Id)).ToList();
        var elsewhere = snapshot.Groups.Where(g => !now.Contains(g.Id) && (g.Synced || g.Dynamic))
            .Select(g => $"{g.Name ?? g.Id} — {(g.Synced ? "synced from AD; comes back with the AD groups" : "dynamic; comes back when the rule matches")}").ToList();
        ctx.Set(PlanKey, cloud);
        if (cloud.Count == 0) return StepPreview.NoOp(Id, "In every cloud group the snapshot recorded") with { Details = elsewhere };

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Add back to {Plural(cloud.Count, "group")}",
            Details = [.. cloud.Select(g => g.Name ?? g.Id), .. elsewhere],
        };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var writes = new List<(string, WriteOutcome)>();
        foreach (var g in ctx.Get<List<SnapshotGroup>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            writes.Add((g.Name ?? g.Id, await groups.AddMemberAsync(g.Id, ctx.UserId, ctx.UserLabel, ct).ConfigureAwait(false)));
        }

        return Combined(Id, n => $"Added back to {Plural(n, "group")}", writes);
    }
}

/// <summary>Sets the manager the snapshot recorded. Cloud accounts only: a synced account's manager is AD's, and offboarding never cleared it there.</summary>
internal sealed class RestoreManagerStep(UserService users, GraphClient graph, ActionLog actions, TenantContext tenant)
    : RestoreStep("restore-manager", "Set the manager again", StepSurface.Graph, Capabilities.EditUser)
{
    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Manager is not { } manager) return StepPreview.NoOp(Id, "The snapshot recorded no manager");
        if (ctx.User.OnPremisesSyncEnabled) return StepPreview.NoOp(Id, "Synced: the manager is mastered in AD, which offboarding left alone");
        var live = await LiveAsync(users, ctx, ct).ConfigureAwait(false);
        if (live.Manager.Value?.Id == manager.Id) return StepPreview.NoOp(Id, $"{manager.Name ?? manager.Id} is the manager");
        return new StepPreview { StepId = Id, Summary = $"Set the manager to {manager.Name ?? manager.Id}" };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var manager = snapshot.Manager!;
        var body = new Dictionary<string, object?> { ["@odata.id"] = $"{GraphClient.V1BaseAddress}directoryObjects/{manager.Id}" };
        await graph.SendAsync(HttpMethod.Put, $"users/{ctx.UserId}/manager/$ref", body, ct).ConfigureAwait(false);
        actions.Record(new ActionRecord
        {
            Actor = tenant.SignedInUpn,
            TenantId = tenant.TenantId,
            TargetType = ObjectType.User,
            TargetId = ctx.UserId,
            TargetName = ctx.UserLabel,
            Action = "user.manager.set",
            RequestSummary = $"PUT manager/$ref ({manager.Id})",
            Outcome = ActionOutcome.Success,
            Message = $"Manager restored to {manager.Name ?? manager.Id}",
        });
        return StepResult.Success(Id, $"Manager set to {manager.Name ?? manager.Id}");
    }
}

/// <summary>Puts back the attributes the stamp step may have overwritten. Cloud accounts only, as the stamp step is.</summary>
internal sealed class RestoreAttributesStep(UserService users) : RestoreStep("restore-attributes", "Put back the job details", StepSurface.Graph, Capabilities.EditUser)
{
    private const string PlanKey = "restore.attributes";

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (ctx.User.OnPremisesSyncEnabled) return StepPreview.NoOp(Id, "Synced: these are mastered in AD, which offboarding left alone");
        var live = await LiveAsync(users, ctx, ct).ConfigureAwait(false);
        var user = live.User.Value ?? ctx.User;
        var now = new Dictionary<string, string?>();
        using (var raw = user.ParseRaw())
        {
            foreach (var name in snapshot.Attributes.Keys) now[name] = raw.RootElement.GetString(name);
        }

        var changes = snapshot.Attributes.Where(a => !string.Equals(a.Value ?? string.Empty, now.GetValueOrDefault(a.Key) ?? string.Empty, StringComparison.Ordinal)).ToList();
        ctx.Set(PlanKey, changes);
        if (changes.Count == 0) return StepPreview.NoOp(Id, "The job details are as the snapshot recorded them");

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Set {Plural(changes.Count, "attribute")} back",
            Details = [.. changes.Select(c => $"{c.Key}: {now.GetValueOrDefault(c.Key) ?? "(empty)"} → {c.Value ?? "(empty)"}")],
        };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var writes = new List<(string, WriteOutcome)>();
        foreach (var (name, value) in ctx.Get<List<KeyValuePair<string, string?>>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            // Graph clears with null, never "" (M9: department rejects an empty string).
            writes.Add((name, await users.UpdateAsync(ctx.UserId, name, string.IsNullOrEmpty(value) ? null : value, ct).ConfigureAwait(false)));
        }

        return Combined(Id, n => $"Set {Plural(n, "attribute")} back", writes);
    }
}
