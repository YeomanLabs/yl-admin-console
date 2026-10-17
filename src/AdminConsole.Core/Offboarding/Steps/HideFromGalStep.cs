using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>Doc 06 step 10, un-greyed in M15: hide the leaver from the address book so nobody picks them by mistake.</summary>
public sealed class HideFromGalStep : IOffboardingStep
{
    private readonly ExchangeService _exchange;

    public HideFromGalStep(ExchangeService exchange) => _exchange = exchange;

    public string Id => "hide-from-gal";
    public string Title => "Hide from address lists";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await ExchangeStep.FindAsync(_exchange, ctx, Id, Title, ct, adMastered: true).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;

        var m = found.Mailbox!;
        return m.HiddenFromAddressLists
            ? StepPreview.NoOp(Id, $"{m.PrimarySmtp ?? ctx.UserLabel} is already hidden from address lists")
            : new StepPreview { StepId = Id, Summary = $"Hide {m.PrimarySmtp ?? ctx.UserLabel} from the address book; mail sent to it still arrives" };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct) =>
        ExchangeStep.Result(Id, await _exchange.SetHiddenAsync(ExchangeStep.Ref(ctx), true, ct).ConfigureAwait(false));
}
