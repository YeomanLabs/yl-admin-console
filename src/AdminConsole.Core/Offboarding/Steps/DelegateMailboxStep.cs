using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 14, un-greyed in M15: Full Access for the delegate, automapped so the mailbox simply appears in their
/// Outlook. With the mailbox converted to shared (step 11) this is how it is read from now on.
/// </summary>
public sealed class DelegateMailboxStep : IOffboardingStep
{
    private readonly ExchangeService _exchange;

    public DelegateMailboxStep(ExchangeService exchange) => _exchange = exchange;

    public string Id => "delegate-mailbox";
    public string Title => "Give the delegate access to the mailbox";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await ExchangeStep.FindAsync(_exchange, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;
        if (ctx.Delegate is not { } d) return StepPreview.Blocked(Id, Title, "Choose a delegate first — access has to go to someone");
        if (d.UserPrincipalName is null) return StepPreview.Blocked(Id, Title, $"{d.Label} has no sign-in name, so Exchange can't be told who to give access to");

        var current = await _exchange.GetFullAccessAsync(ctx.UserId, ct).ConfigureAwait(false);
        if (current.Value?.Any(e => !e.IsDeny && string.Equals(e.Principal, d.UserPrincipalName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return StepPreview.NoOp(Id, $"{d.Label} already has Full Access");
        }

        var others = current.Value?.Where(e => !e.IsDeny).Select(e => e.Principal).ToList() ?? [];
        return new StepPreview
        {
            StepId = Id,
            Summary = $"Give {d.Label} Full Access; the mailbox appears in their Outlook on its own",
            Details = others.Count == 0 ? [] : [$"Already with Full Access: {string.Join(", ", others)}"],
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (ctx.Delegate?.UserPrincipalName is not { } upn) return StepResult.Skipped(Id, "No delegate was chosen");
        return ExchangeStep.Result(Id, await _exchange.GrantFullAccessAsync(ExchangeStep.Ref(ctx), upn, autoMapping: true, ct).ConfigureAwait(false));
    }
}
