using AdminConsole.Core.Exchange;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// What every Exchange step (M15.3) needs first: the permission, the leaver's mailbox, and — for the settings AD masters
/// on a synced mailbox — a reason to stop. Shared the way <see cref="OnPremStep"/> is, so eleven steps fail in the same
/// three words for the same three causes.
/// </summary>
internal static class ExchangeStep
{
    private const string MailboxKey = "exchange.mailbox";

    internal const string NotGranted =
        "Needs the Exchange Online permission, which this installation hasn't been granted. Grant it from the user's Mailbox tab, then preview again.";

    /// <summary>Doc 13 §3 and DECISIONS (M14): addresses, hiding, Send on Behalf and forwarding live in AD for a synced mailbox.</summary>
    internal const string SyncedReason =
        "Synced from on-prem AD, where this setting is mastered: change it in AD and it syncs up. Writing it to AD from here is not built yet.";

    internal readonly record struct Found(MailboxDetail? Mailbox, StepPreview? Stop);

    /// <param name="adMastered">The setting is one AD masters on a synced mailbox, so Exchange would refuse the write.</param>
    internal static async Task<Found> FindAsync(ExchangeService exchange, OffboardingContext ctx, string stepId, string title, CancellationToken ct, bool adMastered = false)
    {
        if (!exchange.IsAvailable) return new Found(null, StepPreview.Blocked(stepId, title, NotGranted));

        ExchangeSection<MailboxDetail> section;
        try
        {
            section = await ctx.GetOrAdd(MailboxKey, () => exchange.GetMailboxAsync(ctx.UserId, ct)).ConfigureAwait(false);
        }
        catch (ExchangeException ex)
        {
            return new Found(null, StepPreview.Error(stepId, title, $"The mailbox could not be read. {ExchangeErrorText.Friendly(ex)}"));
        }

        if (section.IsNotFound) return new Found(null, StepPreview.NoOp(stepId, $"{ctx.UserLabel} has no Exchange Online mailbox"));
        if (!section.IsSuccess) return new Found(null, StepPreview.Error(stepId, title, $"The mailbox could not be read. {section.Message}"));

        var mailbox = section.Value!;
        if (mailbox.Kind is not (MailboxKind.User or MailboxKind.Shared))
        {
            return new Found(null, StepPreview.NoOp(stepId, $"{ctx.UserLabel}'s mailbox is a {MailboxKinds.Describe(mailbox.Kind).ToLowerInvariant()}, which offboarding leaves alone"));
        }

        return adMastered && mailbox.DirSynced
            ? new Found(null, StepPreview.Blocked(stepId, title, SyncedReason))
            : new Found(mailbox, null);
    }

    internal static MailboxRef Ref(OffboardingContext ctx) => new(ctx.UserId, ctx.UserLabel);

    /// <summary>A mailbox write's outcome as a step result, warnings kept: Exchange's own words are never dropped (doc 13 §1).</summary>
    internal static StepResult Result(string stepId, WriteOutcome outcome)
    {
        if (!outcome.Success)
        {
            return StepResult.Failed(stepId, outcome.FriendlyError ?? outcome.Summary) with { ExchangeError = outcome.ExchangeError };
        }

        var message = outcome.Warning is { } warning ? $"{outcome.Summary}. Exchange says: {warning}" : outcome.Summary;
        return StepResult.Success(stepId, message, outcome.Undo);
    }

    /// <summary>Several writes as one step: undo reverses the ones that landed, newest first.</summary>
    /// <param name="describe">The log line for how many landed: <c>n => $"Turned off {n} rules"</c>.</param>
    internal static StepResult Combined(string stepId, Func<int, string> describe, string noun, IReadOnlyList<(string Label, WriteOutcome Outcome)> writes)
    {
        var done = writes.Where(w => w.Outcome.Success && !w.Outcome.WasNoOp).ToList();
        var failed = writes.Where(w => !w.Outcome.Success).ToList();
        var undos = done.Select(w => w.Outcome.Undo).OfType<Func<CancellationToken, Task<WriteOutcome>>>().Reverse().ToList();

        Func<CancellationToken, Task<WriteOutcome>>? undo = undos.Count == 0 ? null : async token =>
        {
            var back = 0;
            foreach (var u in undos)
            {
                if ((await u(token).ConfigureAwait(false)).Success) back++;
            }

            return new WriteOutcome
            {
                Success = back == undos.Count,
                Summary = $"Put back {back} of {undos.Count} {noun}",
                FriendlyError = back == undos.Count ? null : $"{undos.Count - back} could not be put back.",
            };
        };

        var summary = describe(done.Count);
        if (failed.Count == 0) return StepResult.Success(stepId, summary, undo);

        var why = string.Join("; ", failed.Select(f => $"{f.Label}: {f.Outcome.FriendlyError ?? f.Outcome.Summary}"));
        var result = done.Count == 0
            ? StepResult.Failed(stepId, $"None of the {noun} changed. {why}")
            : StepResult.Failed(stepId, $"{summary}, but {failed.Count} failed: {why}") with { Undo = undo };
        return result with { ExchangeError = failed[0].Outcome.ExchangeError };
    }

    internal static string Plural(int n, string noun, string? plural = null) => n == 1 ? $"1 {noun}" : $"{n} {plural ?? noun + "s"}";
}
