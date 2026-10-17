using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Turns off the leaver's inbox rules that send mail somewhere else — forward, redirect, forward as attachment (M15.3).
/// Off, not deleted, so Undo and Restore can turn them back on. Rules that only file or flag mail are left running,
/// because whoever inherits the mailbox inherits its order too.
/// </summary>
public sealed class RemoveInboxRulesStep : IOffboardingStep
{
    private const string PlanKey = "exchange.rules.plan";

    private readonly ExchangeService _exchange;

    public RemoveInboxRulesStep(ExchangeService exchange) => _exchange = exchange;

    public string Id => "remove-inbox-rules";
    public string Title => "Turn off inbox rules that send mail elsewhere";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await ExchangeStep.FindAsync(_exchange, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;

        IReadOnlyList<InboxRuleRef> rules;
        try
        {
            rules = await _exchange.GetInboxRulesAsync(ctx.UserId, ct).ConfigureAwait(false);
        }
        catch (ExchangeException ex)
        {
            return StepPreview.Error(Id, Title, $"The inbox rules could not be read. {ExchangeErrorText.Friendly(ex)}");
        }

        var off = rules.Where(r => r.Enabled && r.SendsMailElsewhere).ToList();
        var kept = rules.Where(r => r.Enabled && !r.SendsMailElsewhere).Select(r => $"{r.Name} — left on: it doesn't send mail anywhere").ToList();
        ctx.Set(PlanKey, off);

        if (off.Count == 0)
        {
            return StepPreview.NoOp(Id, rules.Count == 0 ? "No inbox rules" : "No inbox rule sends mail elsewhere") with { Details = kept };
        }

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Turn off {ExchangeStep.Plural(off.Count, "inbox rule")} that {(off.Count == 1 ? "sends" : "send")} mail elsewhere",
            Details = [.. off.Select(r => r.Name), .. kept],
            Reason = "Turned off, not deleted, so Undo can turn them back on.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var m = ExchangeStep.Ref(ctx);
        var writes = new List<(string, WriteOutcome)>();
        foreach (var rule in ctx.Get<List<InboxRuleRef>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            writes.Add((rule.Name, await _exchange.SetInboxRuleEnabledAsync(m, rule, enabled: false, ct).ConfigureAwait(false)));
        }

        return ExchangeStep.Combined(Id, n => $"Turned off {ExchangeStep.Plural(n, "inbox rule")}", "rules", writes);
    }
}
