using System.Net;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 12, un-greyed in M15: an automatic reply to everyone, inside and out, naming who to contact instead. The
/// text is the profile's own or the template below, and the preview shows it word for word.
/// </summary>
public sealed class SetAutoReplyStep : IOffboardingStep
{
    private const string TextKey = "exchange.autoreply.text";

    private readonly ExchangeService _exchange;

    public SetAutoReplyStep(ExchangeService exchange) => _exchange = exchange;

    public string Id => "set-auto-reply";
    public string Title => "Set an automatic reply";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    /// <summary>Doc 06 step 12's template: the delegate is named when there is one.</summary>
    public static string Template(OffboardingContext ctx)
    {
        var name = ctx.User.DisplayName ?? ctx.UserLabel;
        if (ctx.Delegate is not { } d) return $"Thank you for your message. {name} is no longer with us.";
        var contact = d.UserPrincipalName is { } upn ? $"{d.Label} ({upn})" : d.Label;
        return $"Thank you for your message. {name} is no longer with us; please contact {contact} instead.";
    }

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var found = await ExchangeStep.FindAsync(_exchange, ctx, Id, Title, ct).ConfigureAwait(false);
        if (found.Stop is { } stop) return stop;

        var text = string.IsNullOrWhiteSpace(ctx.AutoReplyMessage) ? Template(ctx) : ctx.AutoReplyMessage.Trim();
        ctx.Set(TextKey, text);

        var current = await _exchange.GetAutoReplyAsync(ctx.UserId, ct).ConfigureAwait(false);
        if (current.Value is { IsOn: true } on && !on.IsScheduled && AutoReplySettings.PlainText(on.InternalMessage) == text
            && AutoReplySettings.PlainText(on.ExternalMessage) == text && on.ExternalAudience == "All")
        {
            return StepPreview.NoOp(Id, "That automatic reply is already on");
        }

        return new StepPreview
        {
            StepId = Id,
            Summary = "Reply to everyone who writes, inside and outside the organisation",
            Details = [$"“{text}”"],
            Reason = current.Value is { IsOn: true } ? "An automatic reply is already on; this replaces it." : null,
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var text = ctx.Get<string>(TextKey) ?? Template(ctx);
        var html = WebUtility.HtmlEncode(text);
        var desired = new AutoReplySettings { State = "Enabled", InternalMessage = html, ExternalMessage = html, ExternalAudience = "All" };
        return ExchangeStep.Result(Id, await _exchange.SetAutoReplyAsync(ExchangeStep.Ref(ctx), desired, ct).ConfigureAwait(false));
    }
}
