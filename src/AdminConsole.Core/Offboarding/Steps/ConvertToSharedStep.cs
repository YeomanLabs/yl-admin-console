using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 11, un-greyed in M15: the leaver's mailbox becomes a shared mailbox, which needs no licence, so the mail
/// survives the licence step. It runs ahead of that step for exactly this reason (M15.3).
/// </summary>
/// <remarks>
/// A shared mailbox stops being free when it is over 50 GB, has an archive or is on hold; each of those keeps needing a
/// licence, and the preview says so rather than letting the licence step quietly strand the mailbox.
/// </remarks>
public sealed class ConvertToSharedStep : IOffboardingStep
{
    /// <summary>Microsoft's limit for an unlicensed shared mailbox.</summary>
    internal const long FreeSharedLimitBytes = 50L * 1024 * 1024 * 1024;

    private readonly ExchangeService _exchange;

    public ConvertToSharedStep(ExchangeService exchange) => _exchange = exchange;

    public string Id => "convert-to-shared";
    public string Title => "Convert the mailbox to shared";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await ExchangeStep.FindAsync(_exchange, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;

        var m = found.Mailbox!;
        if (m.Kind == MailboxKind.Shared) return StepPreview.NoOp(Id, "The mailbox is already shared");

        var notes = new List<string>
        {
            "Runs before the licence step: a mailbox that loses its licence first starts a 30-day countdown to deletion.",
        };

        var usage = await _exchange.GetUsageAsync(ctx.UserId, archive: false, ct).ConfigureAwait(false);
        var size = usage.Value?.TotalItemSize;
        if (size?.Bytes is > FreeSharedLimitBytes)
        {
            notes.Add($"It holds {size.Value.ToDisplay()}: a shared mailbox over 50 GB still needs a licence, so keep one assigned.");
        }

        if (m.HasArchive) notes.Add("It has an archive, which keeps needing an Exchange Online Plan 2 or Archiving licence once shared.");
        if (m.LitigationHold) notes.Add("It is on litigation hold, which keeps needing a licence once shared.");
        if (m.DirSynced) notes.Add("Hybrid: also run Set-RemoteMailbox -Type Shared on-prem, so the two sides agree.");

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Convert {m.PrimarySmtp ?? ctx.UserLabel} to a shared mailbox" + (size is { IsKnown: true } s ? $" ({s.ToDisplay()})" : string.Empty),
            Reason = string.Join(" ", notes),
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct) =>
        ExchangeStep.Result(Id, await _exchange.ConvertAsync(ExchangeStep.Ref(ctx), toShared: true, ct).ConfigureAwait(false));
}
