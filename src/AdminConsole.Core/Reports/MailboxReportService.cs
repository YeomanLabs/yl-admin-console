using Microsoft.Extensions.Logging;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Reports;

/// <summary>
/// M19.3 — size and delegation for every mailbox. Send As and Send on Behalf come from two tenant-wide calls; size and
/// Full Access have no tenant-wide form, so they are read per mailbox, four at a time, with progress. This is why the
/// report is built on request and kept, rather than run every time the page opens.
/// </summary>
public sealed class MailboxReportService
{
    public const string Kind = "mailboxes";
    private const int Parallelism = 4;

    private readonly ExchangeService _exchange;
    private readonly CacheDb _db;
    private readonly ILogger<MailboxReportService> _logger;

    public MailboxReportService(ExchangeService exchange, CacheDb db, ILogger<MailboxReportService> logger)
    {
        _exchange = exchange;
        _db = db;
        _logger = logger;
    }

    public async Task<MailboxReport> BuildAsync(IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var tenantWide = await _exchange.GetDelegationOverviewAsync(ct).ConfigureAwait(false);
        var mailboxes = tenantWide.Mailboxes;
        var rows = new MailboxReportRow[mailboxes.Count];
        var done = 0;
        progress?.Report((0, mailboxes.Count));

        await Parallel.ForEachAsync(Enumerable.Range(0, mailboxes.Count), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var m = mailboxes[i];
                var usage = await _exchange.GetUsageAsync(m.UserId, archive: false, token).ConfigureAwait(false);
                var full = await _exchange.GetFullAccessAsync(m.UserId, token).ConfigureAwait(false);
                var problems = new[] { usage.Message is { } ue ? "size: " + ue : null, full.Message is { } fe ? "Full Access: " + fe : null }
                    .OfType<string>().ToList();
                rows[i] = new MailboxReportRow
                {
                    UserId = m.UserId,
                    Name = _db.GetUser(m.UserId)?.DisplayName ?? m.Name,
                    Address = m.PrimarySmtp,
                    Kind = m.Kind,
                    SizeBytes = usage.Value?.TotalItemSize.Bytes,
                    Items = usage.Value?.ItemCount,
                    LastUserAction = usage.Value?.LastUserAction,
                    FullAccess = [.. (full.Value ?? []).Where(e => !e.IsDeny && e.Rights.Contains("FullAccess", StringComparer.OrdinalIgnoreCase)).Select(e => e.Principal)],
                    SendAs = tenantWide.SendAs.GetValueOrDefault(m.Identity) ?? [],
                    SendOnBehalf = m.SendOnBehalf,
                    Problem = problems.Count == 0 ? null : string.Join("; ", problems),
                };
                progress?.Report((Interlocked.Increment(ref done), mailboxes.Count));
            }).ConfigureAwait(false);

        _logger.LogInformation("Mailbox report: {Count} mailboxes, {Problems} with problems", rows.Length, rows.Count(r => r.Problem is not null));
        return new MailboxReport
        {
            TakenAt = DateTimeOffset.Now,
            Rows = [.. rows.OrderByDescending(r => r.SizeBytes ?? -1).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)],
        };
    }
}
