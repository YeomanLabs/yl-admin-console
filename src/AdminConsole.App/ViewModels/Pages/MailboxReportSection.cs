using System.Globalization;
using Microsoft.Extensions.Logging;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Reports;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>M19.3 on the Reports page.</summary>
public sealed class MailboxReportSection : ReportSection<MailboxReport, MailboxReportRow>
{
    private readonly MailboxReportService _service;

    public MailboxReportSection(MailboxReportService service, ReportStore store, TenantContext tenant, ConsentService consent, PermissionChecker permissions, ILogger<MailboxReportSection> logger)
        : base(store, tenant, consent, permissions, MailboxReportService.Kind, logger) => _service = service;

    public override string Title => "Mailboxes";

    public override string Description =>
        "Every mailbox's size and who else can open it or send from it. Size and Full Access are read one mailbox at a time, " +
        "so the report is built when you ask and kept, dated, rather than run each time the page opens.";

    public override IReadOnlyList<ChoiceItem> Shows { get; } =
    [
        new("all", "Every mailbox"),
        new("delegated", "Delegated"),
        new("shared", "Shared and resource"),
        new("user", "User mailboxes"),
        new("problems", "Could not be read"),
    ];

    protected override Capability? RequiredCapability => Capabilities.ManageMailboxes;

    protected override DateTimeOffset TakenAtOf(MailboxReport report) => report.TakenAt;

    protected override async Task<MailboxReport> BuildCoreAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Listing mailboxes and Send As grants…");
        var counter = new Progress<(int Done, int Total)>(p => progress.Report($"Reading mailbox {p.Done:N0} of {p.Total:N0}…"));
        return await _service.BuildAsync(counter, ct).ConfigureAwait(false);
    }

    protected override IEnumerable<ReportFigure> FiguresOf(MailboxReport r) =>
    [
        new("Mailboxes", r.Rows.Count.ToString("N0", CultureInfo.CurrentCulture)),
        new("Total size", new ExchangeSize(r.TotalBytes, false).ToDisplay()),
        new("Delegated", r.Delegated.ToString("N0", CultureInfo.CurrentCulture)),
        new("Could not be read", r.Problems.ToString("N0", CultureInfo.CurrentCulture), r.Problems > 0),
    ];

    protected override string? NoteOf(MailboxReport r) => null;

    protected override IEnumerable<MailboxReportRow> RowsOf(MailboxReport r, string show) => show switch
    {
        "delegated" => r.Rows.Where(x => x.IsDelegated),
        "shared" => r.Rows.Where(x => x.Kind is MailboxKind.Shared or MailboxKind.Room or MailboxKind.Equipment),
        "user" => r.Rows.Where(x => x.Kind == MailboxKind.User),
        "problems" => r.Rows.Where(x => x.Problem is not null),
        _ => r.Rows,
    };

    protected override IReadOnlyList<string> ExportHeaders { get; } =
        ["Mailbox", "Address", "Kind", "Size (bytes)", "Size", "Items", "Last user action", "Full Access", "Send As", "Send on behalf", "Problem"];

    protected override IReadOnlyList<string?> ExportCells(MailboxReportRow r) =>
    [
        r.Name, r.Address, r.Kind.ToString(), r.SizeBytes?.ToString(CultureInfo.InvariantCulture), r.SizeText, r.Items?.ToString(CultureInfo.InvariantCulture),
        r.LastUserAction?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string.Join("; ", r.FullAccess), string.Join("; ", r.SendAs), string.Join("; ", r.SendOnBehalf), r.Problem,
    ];
}
