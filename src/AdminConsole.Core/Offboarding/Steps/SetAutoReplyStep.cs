using System.Net;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 12, un-greyed in M15: an automatic reply to everyone, inside and out, naming who to contact instead. The
/// text is the offboarding template's own, with its placeholders filled in (<see cref="AutoReplyText"/>), or the
/// standard text below, and the preview shows it word for word.
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

        var custom = ctx.AutoReplyMessage?.Trim();
        if (!string.IsNullOrEmpty(custom) && ctx.Delegate is null && AutoReplyText.NeedsDelegate(custom))
        {
            return StepPreview.Blocked(Id, Title, "This template's automatic reply names the delegate. Choose a delegate, or untick this step.");
        }

        var text = string.IsNullOrEmpty(custom) ? Template(ctx) : AutoReplyText.Render(custom, AutoReplyText.For(ctx));
        ctx.Set(TextKey, text);

        var current = await _exchange.GetAutoReplyAsync(ctx.UserId, ct).ConfigureAwait(false);
        // Compared as Exchange keeps it: tags gone, whitespace collapsed, so a reply of several lines is still recognised.
        var plain = AutoReplySettings.PlainText(Html(text));
        if (current.Value is { IsOn: true } on && !on.IsScheduled && AutoReplySettings.PlainText(on.InternalMessage) == plain
            && AutoReplySettings.PlainText(on.ExternalMessage) == plain && on.ExternalAudience == "All")
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
        var html = Html(text);
        var desired = new AutoReplySettings { State = "Enabled", InternalMessage = html, ExternalMessage = html, ExternalAudience = "All" };
        return ExchangeStep.Result(Id, await _exchange.SetAutoReplyAsync(ExchangeStep.Ref(ctx), desired, ct).ConfigureAwait(false));
    }

    /// <summary>The plain text as the HTML Exchange sends: encoded, and a template's line breaks kept as line breaks.</summary>
    private static string Html(string text) => WebUtility.HtmlEncode(text).ReplaceLineEndings("<br>");
}
