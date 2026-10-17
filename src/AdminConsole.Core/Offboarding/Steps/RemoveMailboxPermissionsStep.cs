using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Takes away the leaver's access to <em>other</em> mailboxes (M15.3): Send As anywhere in the tenant, and Full Access on
/// shared mailboxes. A disabled account can use neither, but a rehire would walk straight back into both, and the
/// mailboxes keep listing a person who left.
/// </summary>
/// <remarks>
/// Send As is one tenant-wide read. Full Access has no such read — Exchange answers it per mailbox — so the step checks
/// the shared mailboxes in the cache, where delegation lives, up to <see cref="FullAccessScanLimit"/>, and says how many
/// it checked. User mailboxes delegated to an assistant are not scanned; on a tenant this size that would be thousands
/// of calls inside a preview.
/// </remarks>
public sealed class RemoveMailboxPermissionsStep : IOffboardingStep
{
    internal const int FullAccessScanLimit = 100;
    private const int Parallelism = 4;
    private const string PlanKey = "exchange.permissions.plan";

    private readonly ExchangeService _exchange;
    private readonly CacheDb _db;

    public RemoveMailboxPermissionsStep(ExchangeService exchange, CacheDb db)
    {
        _exchange = exchange;
        _db = db;
    }

    public string Id => "remove-mailbox-permissions";
    public string Title => "Remove access to other mailboxes";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (!_exchange.IsAvailable) return StepPreview.Blocked(Id, Title, ExchangeStep.NotGranted);
        if (ctx.User.UserPrincipalName is not { } upn) return StepPreview.Blocked(Id, Title, "The account has no sign-in name to look up permissions by");

        IReadOnlyList<RecipientRef> sendAs;
        try
        {
            sendAs = await _exchange.GetSendAsGrantedToAsync(upn, ct).ConfigureAwait(false);
        }
        catch (ExchangeException ex)
        {
            return StepPreview.Error(Id, Title, $"Send As permissions could not be read. {ExchangeErrorText.Friendly(ex)}");
        }

        var shared = _db.GetMailboxes(MailboxKinds.SharedMailbox).Where(m => m.UserId != ctx.UserId).ToList();
        var scanned = shared.Take(FullAccessScanLimit).ToList();
        var fullAccess = await ScanFullAccessAsync(scanned, upn, ct).ConfigureAwait(false);

        var plan = new List<Grant>();
        var notes = new List<string>();
        foreach (var r in sendAs.Where(r => r.ObjectId != ctx.UserId))
        {
            if (r.ObjectId is { } id && r.RecipientType?.Contains("Mailbox", StringComparison.OrdinalIgnoreCase) == true) plan.Add(new Grant(id, r.Display, SendAs: true));
            else notes.Add($"Send As on {r.Display} — not a mailbox here; remove it in the Exchange admin centre");
        }

        plan.AddRange(fullAccess.Found.Select(m => new Grant(m.UserId, m.PrimarySmtp ?? m.UserId, SendAs: false)));
        notes.AddRange(fullAccess.Unreadable.Select(m => $"{m} — Full Access couldn't be read"));
        if (shared.Count > scanned.Count) notes.Add($"Full Access checked on the first {scanned.Count} of {shared.Count} shared mailboxes");
        if (shared.Count == 0) notes.Add("No shared mailboxes are cached yet, so Full Access was not checked — refresh mailboxes from Settings");

        ctx.Set(PlanKey, plan);
        if (plan.Count == 0)
        {
            return StepPreview.NoOp(Id, $"No Send As anywhere, and no Full Access on {ExchangeStep.Plural(scanned.Count, "shared mailbox", "shared mailboxes")}")
                with { Details = notes };
        }

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Remove {ExchangeStep.Plural(plan.Count, "permission")} on other mailboxes",
            Details = [.. plan.Select(g => $"{(g.SendAs ? "Send As" : "Full Access")} on {g.Label}"), .. notes],
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var plan = ctx.Get<List<Grant>>(PlanKey) ?? [];
        var upn = ctx.User.UserPrincipalName!;
        var writes = new List<(string, WriteOutcome)>();
        foreach (var g in plan)
        {
            ct.ThrowIfCancellationRequested();
            var target = new MailboxRef(g.MailboxId, g.Label);
            var outcome = g.SendAs
                ? await _exchange.RevokeSendAsAsync(target, upn, ct).ConfigureAwait(false)
                : await _exchange.RevokeFullAccessAsync(target, upn, ct).ConfigureAwait(false);
            writes.Add(($"{(g.SendAs ? "Send As" : "Full Access")} on {g.Label}", outcome));
        }

        return ExchangeStep.Combined(Id, n => $"Removed {ExchangeStep.Plural(n, "permission")} on other mailboxes", "permissions", writes);
    }

    private async Task<(List<MailboxSummary> Found, List<string> Unreadable)> ScanFullAccessAsync(IReadOnlyList<MailboxSummary> mailboxes, string upn, CancellationToken ct)
    {
        var found = new List<MailboxSummary>();
        var unreadable = new List<string>();
        using var gate = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(mailboxes.Select(async m =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var entries = await _exchange.GetFullAccessAsync(m.UserId, ct).ConfigureAwait(false);
                lock (found)
                {
                    if (!entries.IsSuccess) unreadable.Add(m.PrimarySmtp ?? m.UserId);
                    else if (entries.Value!.Any(e => !e.IsDeny && string.Equals(e.Principal, upn, StringComparison.OrdinalIgnoreCase))) found.Add(m);
                }
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        found.Sort((a, b) => string.Compare(a.PrimarySmtp, b.PrimarySmtp, StringComparison.OrdinalIgnoreCase));
        return (found, unreadable);
    }

    private sealed record Grant(string MailboxId, string Label, bool SendAs);
}
