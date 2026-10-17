using System.Text.Json;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Restore;

/// <summary>Shared mailbox back to a user mailbox. Runs after the licences, because a user mailbox needs one.</summary>
internal sealed class RestoreMailboxTypeStep(ExchangeService exchange) : RestoreStep("restore-mailbox-type", "Turn the mailbox back into a user mailbox", StepSurface.Exchange, Capabilities.ManageMailboxes)
{
    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Mailbox is not { } recorded) return StepPreview.NoOp(Id, "The snapshot recorded no mailbox");
        if (MailboxKinds.From(recorded.RecipientType) != MailboxKind.User) return StepPreview.NoOp(Id, "The mailbox was not a user mailbox when the snapshot was taken");
        var found = await ExchangeStep.FindAsync(exchange, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;
        if (found.Mailbox!.Kind == MailboxKind.User) return StepPreview.NoOp(Id, "It is a user mailbox");
        return new StepPreview { StepId = Id, Summary = "Convert the shared mailbox back to a user mailbox", Reason = "Runs after the licences: a user mailbox needs one within 30 days." };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct) =>
        ExchangeStep.Result(Id, await exchange.ConvertAsync(ExchangeStep.Ref(ctx), toShared: false, ct).ConfigureAwait(false));
}

/// <summary>Distribution lists the account left, back through Exchange — Graph cannot add members to them.</summary>
internal sealed class RestoreDistributionListsStep(ExchangeService exchange, Users.UserService users) : RestoreStep("restore-distribution-lists", "Put back the distribution lists", StepSurface.Exchange, Capabilities.ManageMailboxes)
{
    private const string PlanKey = "restore.lists";

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var recorded = snapshot.Groups.Where(g => g.MailEnabledList && !g.Synced).ToList();
        if (recorded.Count == 0) return StepPreview.NoOp(Id, "The snapshot recorded no distribution lists");
        if (!exchange.IsAvailable) return StepPreview.Blocked(Id, Title, ExchangeStep.NotGranted);

        // The restore's shared live read holds today's memberships, Graph's view of the lists included.
        var live = await LiveAsync(users, ctx, ct).ConfigureAwait(false);
        if (live.Groups.Error is { } error) return StepPreview.Error(Id, Title, Graph.GraphErrorText.Friendly(error));
        var now = (live.Groups.Value ?? []).Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = recorded.Where(g => !now.Contains(g.Id)).ToList();
        ctx.Set(PlanKey, missing);
        if (missing.Count == 0) return StepPreview.NoOp(Id, "In every distribution list the snapshot recorded");
        return new StepPreview { StepId = Id, Summary = $"Add back to {Plural(missing.Count, "distribution list")}", Details = [.. missing.Select(g => g.Name ?? g.Id)] };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var m = ExchangeStep.Ref(ctx);
        var writes = new List<(string, WriteOutcome)>();
        foreach (var g in ctx.Get<List<SnapshotGroup>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            writes.Add((g.Name ?? g.Id, await exchange.SetDistributionGroupMemberAsync(m, g.Id, g.Name ?? g.Id, member: true, ct).ConfigureAwait(false)));
        }

        return Combined(Id, n => $"Added back to {Plural(n, "distribution list")}", writes);
    }
}

/// <summary>
/// The mailbox's own settings, back as the snapshot recorded them: shown in address lists, forwarding, automatic
/// replies, the inbox rules offboarding turned off, and Full Access granted since (the delegate's, usually).
/// </summary>
/// <remarks>Litigation hold is left alone: taking a mailbox off hold is a legal decision, not a restore.</remarks>
internal sealed class RestoreMailboxSettingsStep(ExchangeService exchange) : RestoreStep("restore-mailbox-settings", "Put back the mailbox's settings", StepSurface.Exchange, Capabilities.ManageMailboxes)
{
    private const string PlanKey = "restore.mailbox.plan";

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Mailbox is not { } recorded) return StepPreview.NoOp(Id, "The snapshot recorded no mailbox");
        var found = await ExchangeStep.FindAsync(exchange, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;
        var now = found.Mailbox!;
        var plan = new List<Change>();
        var notes = new List<string>();

        if (now.HiddenFromAddressLists && !recorded.HiddenFromAddressLists)
        {
            if (now.DirSynced) notes.Add("Hidden from address lists — synced: show it again in AD");
            else plan.Add(new Change("Show in address lists again", (m, token) => exchange.SetHiddenAsync(m, false, token)));
        }

        var forwardNow = (now.ForwardingAddress, Bare(now.ForwardingSmtpAddress));
        var forwardThen = (recorded.ForwardingAddress, Bare(recorded.ForwardingSmtpAddress));
        if (!Same(forwardNow.Item1, forwardThen.Item1) || !Same(forwardNow.Item2, forwardThen.Item2))
        {
            var then = forwardThen.Item1 ?? forwardThen.Item2;
            var label = then is null ? "Stop forwarding" : $"Forward to {then} again";
            if (now.DirSynced) notes.Add($"{label} — synced: forwarding is set in AD");
            else plan.Add(new Change(label, (m, token) => exchange.SetForwardingAsync(m, recorded.ForwardingAddress, forwardThen.Item2, recorded.DeliverToMailboxAndForward, then, token)));
        }

        var autoReply = await exchange.GetAutoReplyAsync(ctx.UserId, ct).ConfigureAwait(false);
        if (autoReply.Value is { IsOn: true } on && string.Equals(recorded.AutoReplyState, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            plan.Add(new Change("Turn off the automatic reply", (m, token) => exchange.SetAutoReplyAsync(m, on with { State = "Disabled" }, token)));
        }

        if (recorded.EnabledInboxRules.Count > 0)
        {
            var rules = await exchange.GetInboxRulesAsync(ctx.UserId, ct).ConfigureAwait(false);
            foreach (var rule in rules.Where(r => !r.Enabled && recorded.EnabledInboxRules.Contains(r.Name, StringComparer.Ordinal)))
            {
                plan.Add(new Change($"Turn the rule \"{rule.Name}\" back on", (m, token) => exchange.SetInboxRuleEnabledAsync(m, rule, true, token)));
            }
        }

        var fullAccess = await exchange.GetFullAccessAsync(ctx.UserId, ct).ConfigureAwait(false);
        foreach (var e in (fullAccess.Value ?? []).Where(e => !e.IsDeny && !recorded.FullAccess.Contains(e.Principal, StringComparer.OrdinalIgnoreCase)))
        {
            plan.Add(new Change($"Remove {e.Principal}'s Full Access, granted since the snapshot", (m, token) => exchange.RevokeFullAccessAsync(m, e.Principal, token)));
        }

        foreach (var principal in recorded.FullAccess.Where(p => fullAccess.Value?.Any(e => Same(e.Principal, p)) == false))
        {
            plan.Add(new Change($"Give {principal} Full Access again", (m, token) => exchange.GrantFullAccessAsync(m, principal, autoMapping: true, token)));
        }

        if (recorded.LitigationHold != now.LitigationHold && now.LitigationHold) notes.Add("Litigation hold is left on: taking a mailbox off hold is a legal decision");

        ctx.Set(PlanKey, plan);
        if (plan.Count == 0) return StepPreview.NoOp(Id, "The mailbox's settings are as the snapshot recorded them") with { Details = notes };
        return new StepPreview { StepId = Id, Summary = $"Put back {Plural(plan.Count, "mailbox setting")}", Details = [.. plan.Select(c => c.Label), .. notes] };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var m = ExchangeStep.Ref(ctx);
        var writes = new List<(string, WriteOutcome)>();
        foreach (var change in ctx.Get<List<Change>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            writes.Add((change.Label, await change.Apply(m, ct).ConfigureAwait(false)));
        }

        return Combined(Id, n => $"Put back {Plural(n, "mailbox setting")}", writes);
    }

    private static bool Same(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string? Bare(string? smtp) =>
        smtp is not null && smtp.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? smtp[5..] : smtp;

    private sealed record Change(string Label, Func<MailboxRef, CancellationToken, Task<WriteOutcome>> Apply);
}

/// <summary>
/// The access to other mailboxes that the run took away, given back. The snapshot does not hold it — offboarding found
/// it per mailbox — so it comes from the run's own action-log rows, matched by the snapshot's correlation id.
/// </summary>
internal sealed class RestoreOtherMailboxAccessStep(ExchangeService exchange, ActionLog actions) : RestoreStep("restore-other-mailbox-access", "Give back access to other mailboxes", StepSurface.Exchange, Capabilities.ManageMailboxes)
{
    private const string PlanKey = "restore.other.plan";

    protected override async Task<StepPreview> PreviewAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.CorrelationId is null) return StepPreview.NoOp(Id, "The snapshot isn't tied to a run, so there is nothing to look up");
        if (!exchange.IsAvailable) return StepPreview.Blocked(Id, Title, ExchangeStep.NotGranted);

        var removed = actions.Query(new ActionLogQuery { CorrelationId = snapshot.CorrelationId, ActionPrefix = "exchange.", Outcome = ActionOutcome.Success })
            .Where(a => a.UndoneAt is null && a.TargetId != ctx.UserId && a.Action is "exchange.sendas.remove" or "exchange.fullaccess.remove")
            .Select(a => (Row: a, Principal: PrincipalOf(a.InversePayload)))
            .Where(a => a.Principal is not null)
            .Select(r => new Grant(r.Row.Id, r.Row.TargetId, r.Row.TargetName ?? r.Row.TargetId, r.Principal!, r.Row.Action == "exchange.sendas.remove"))
            .ToList();
        if (removed.Count == 0) return StepPreview.NoOp(Id, "The run took no access to other mailboxes away");

        // The log says what was taken; only the mailbox says whether it has come back since. Found at the M15 gate: a
        // second restore offered to give back what the first already had.
        var plan = new List<Grant>();
        foreach (var g in removed)
        {
            var now = g.SendAs ? await exchange.GetSendAsAsync(g.MailboxId, ct).ConfigureAwait(false) : await exchange.GetFullAccessAsync(g.MailboxId, ct).ConfigureAwait(false);
            if (now.Value?.Any(e => !e.IsDeny && string.Equals(e.Principal, g.Principal, StringComparison.OrdinalIgnoreCase)) != true) plan.Add(g);
        }

        ctx.Set(PlanKey, plan);
        if (plan.Count == 0) return StepPreview.NoOp(Id, $"The {Plural(removed.Count, "permission")} the run took away {(removed.Count == 1 ? "is" : "are")} back");

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Give back {Plural(plan.Count, "permission")} on other mailboxes",
            Details = [.. plan.Select(g => $"{(g.SendAs ? "Send As" : "Full Access")} on {g.Label}")],
        };
    }

    protected override async Task<StepResult> ExecuteAsync(OffboardingContext ctx, OffboardingSnapshot snapshot, CancellationToken ct)
    {
        var writes = new List<(string, WriteOutcome)>();
        foreach (var g in ctx.Get<List<Grant>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var target = new MailboxRef(g.MailboxId, g.Label);
            var outcome = g.SendAs
                ? await exchange.GrantSendAsAsync(target, g.Principal, ct).ConfigureAwait(false)
                : await exchange.GrantFullAccessAsync(target, g.Principal, autoMapping: true, ct).ConfigureAwait(false);

            // The offboarding's row is reversed now; the Activity page should say so rather than offer to undo it again.
            if (outcome.Success) actions.MarkUndone(g.RemovalRowId);
            writes.Add(($"{(g.SendAs ? "Send As" : "Full Access")} on {g.Label}", outcome));
        }

        return Combined(Id, n => $"Gave back {Plural(n, "permission")} on other mailboxes", writes);
    }

    private static string? PrincipalOf(string? payload)
    {
        if (payload is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("principal", out var p) ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Grant(long RemovalRowId, string MailboxId, string Label, string Principal, bool SendAs);
}
