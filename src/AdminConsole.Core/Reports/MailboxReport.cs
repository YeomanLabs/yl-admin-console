namespace AdminConsole.Core.Reports;

/// <summary>M19.3: every mailbox's size and who else can open or send from it, cached and dated.</summary>
public sealed record MailboxReport
{
    public required DateTimeOffset TakenAt { get; init; }

    public IReadOnlyList<MailboxReportRow> Rows { get; init; } = [];

    public long TotalBytes => Rows.Sum(r => r.SizeBytes ?? 0);

    public int Delegated => Rows.Count(r => r.IsDelegated);

    public int Problems => Rows.Count(r => r.Problem is not null);
}
