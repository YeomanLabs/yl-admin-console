using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Removes the phones and tablets syncing the leaver's mailbox (M15.3) — the partnership only; nothing on the device is
/// wiped. Intune-managed devices are retired by step 8; this catches the personal phone with Outlook on it.
/// </summary>
public sealed class RemoveMobileDevicesStep : IOffboardingStep
{
    private const string PlanKey = "exchange.mobile.plan";

    private readonly ExchangeService _exchange;

    public RemoveMobileDevicesStep(ExchangeService exchange) => _exchange = exchange;

    public string Id => "remove-mobile-devices";
    public string Title => "Remove phones syncing the mailbox";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await ExchangeStep.FindAsync(_exchange, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;

        IReadOnlyList<MobileDeviceRef> devices;
        try
        {
            devices = await _exchange.GetMobileDevicesAsync(ctx.UserId, ct).ConfigureAwait(false);
        }
        catch (ExchangeException ex)
        {
            return StepPreview.Error(Id, Title, $"The mailbox's mobile devices could not be read. {ExchangeErrorText.Friendly(ex)}");
        }

        ctx.Set(PlanKey, devices.ToList());
        if (devices.Count == 0) return StepPreview.NoOp(Id, "No phone or tablet syncs this mailbox");

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Remove {ExchangeStep.Plural(devices.Count, "device")} from the mailbox",
            Details = [.. devices.Select(d => d.FirstSync is { } first ? $"{d.Display} — syncing since {first.LocalDateTime:d MMM yyyy}" : d.Display)],
            Reason = "Nothing on the device is wiped. Not undoable: a device comes back only by syncing again.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var m = ExchangeStep.Ref(ctx);
        var writes = new List<(string, WriteOutcome)>();
        foreach (var device in ctx.Get<List<MobileDeviceRef>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            writes.Add((device.Display, await _exchange.RemoveMobileDeviceAsync(m, device, ct).ConfigureAwait(false)));
        }

        return ExchangeStep.Combined(Id, n => $"Removed {ExchangeStep.Plural(n, "device")} from the mailbox", "devices", writes);
    }
}
