using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Cancels the meetings the leaver organises from today on (M15.3), so attendees are not left holding recurring
/// invitations nobody can change. Off by default and not undoable: every attendee is told, and that cannot be unsent.
/// </summary>
public sealed class CancelOrganizedMeetingsStep : IOffboardingStep
{
    private readonly ExchangeService _exchange;

    public CancelOrganizedMeetingsStep(ExchangeService exchange) => _exchange = exchange;

    public string Id => "cancel-organized-meetings";
    public string Title => "Cancel the meetings they organise";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await ExchangeStep.FindAsync(_exchange, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;

        IReadOnlyList<string> meetings;
        try
        {
            meetings = await _exchange.PreviewOrganisedMeetingsAsync(ctx.UserId, ct).ConfigureAwait(false);
        }
        catch (ExchangeException ex)
        {
            return StepPreview.Error(Id, Title, $"The calendar could not be read. {ExchangeErrorText.Friendly(ex)}");
        }

        if (meetings.Count == 0) return StepPreview.NoOp(Id, "Organises no meetings from today on");

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Cancel {ExchangeStep.Plural(meetings.Count, "meeting")} they organise from today on",
            Details = [.. meetings.Take(20), .. meetings.Count > 20 ? [$"… and {meetings.Count - 20} more"] : Array.Empty<string>()],
            Reason = "Every attendee gets a cancellation. This can't be undone.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct) =>
        ExchangeStep.Result(Id, await _exchange.CancelOrganisedMeetingsAsync(ExchangeStep.Ref(ctx), ct).ConfigureAwait(false));
}
