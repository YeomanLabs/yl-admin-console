using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 13, un-greyed in M15: forward the leaver's mail to the delegate and keep a copy in the mailbox. With no
/// delegate it still has one job — stopping forwarding to an outside address, the classic way a leaver takes mail along.
/// </summary>
public sealed class ForwardMailStep : IOffboardingStep
{
    private readonly ExchangeService _exchange;

    public ForwardMailStep(ExchangeService exchange) => _exchange = exchange;

    public string Id => "forward-mail";
    public string Title => "Forward mail to the delegate";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await ExchangeStep.FindAsync(_exchange, ctx, Id, Title, ct, adMastered: true).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;

        var m = found.Mailbox!;
        var current = m.ForwardingAddress ?? Bare(m.ForwardingSmtpAddress);
        var external = m.ForwardingSmtpAddress is not null;

        if (ctx.Delegate is not { } d)
        {
            return external
                ? new StepPreview { StepId = Id, Summary = $"Stop forwarding to {current}, outside the organisation", Reason = "No delegate chosen, so mail is not forwarded anywhere else." }
                : StepPreview.Blocked(Id, Title, "Choose a delegate first — forwarding needs someone to forward to");
        }

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Forward new mail to {d.Label}, keeping a copy in the mailbox",
            Reason = current is null ? null : $"It forwards to {current}{(external ? ", outside the organisation" : string.Empty)} now; this replaces that.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var m = ExchangeStep.Ref(ctx);
        var outcome = ctx.Delegate is { } d
            ? await _exchange.SetForwardingAsync(m, d.Id, null, keepCopy: true, d.Label, ct).ConfigureAwait(false)
            : await _exchange.SetForwardingAsync(m, null, null, keepCopy: false, null, ct).ConfigureAwait(false);
        return ExchangeStep.Result(Id, outcome);
    }

    private static string? Bare(string? smtp) =>
        smtp is not null && smtp.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? smtp[5..] : smtp;
}
