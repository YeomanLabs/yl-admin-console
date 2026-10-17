using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Puts the leaver's mailbox on litigation hold (M15.3), off by default: holds are a legal decision, not a tidy-up. It
/// runs ahead of the licence step because applying a hold needs the mailbox's Exchange Online Plan 2 licence.
/// </summary>
public sealed class LitigationHoldStep : IOffboardingStep
{
    private readonly ExchangeService _exchange;

    public LitigationHoldStep(ExchangeService exchange) => _exchange = exchange;

    public string Id => "litigation-hold";
    public string Title => "Put the mailbox on litigation hold";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await ExchangeStep.FindAsync(_exchange, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;

        var m = found.Mailbox!;
        if (m.LitigationHold)
        {
            return StepPreview.NoOp(Id, $"Already on litigation hold ({(m.LitigationHoldDuration is { } d && d != "Unlimited" ? $"{d} days" : "indefinitely")})");
        }

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Hold everything in {m.PrimarySmtp ?? ctx.UserLabel} indefinitely, including what is deleted from now on",
            Reason = "Runs before the licence step: a hold needs Exchange Online Plan 2 to apply, and a held shared mailbox keeps needing it.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct) =>
        ExchangeStep.Result(Id, await _exchange.SetLitigationHoldAsync(ExchangeStep.Ref(ctx), enabled: true, days: null, ct).ConfigureAwait(false));
}
