using System.Globalization;
using Microsoft.Extensions.Logging;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Reports;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>M19.2 on the Reports page. Rows are subscriptions; the flagged accounts are their own list beneath.</summary>
public sealed class LicenceReportSection : ReportSection<LicenceReport, object>
{
    private readonly LicenceReportService _service;

    public LicenceReportSection(LicenceReportService service, ReportStore store, TenantContext tenant, ConsentService consent, PermissionChecker permissions, ILogger<LicenceReportSection> logger)
        : base(store, tenant, consent, permissions, LicenceReportService.Kind, logger) => _service = service;

    public override string Title => "Licences";

    public override string Description =>
        "Seats bought and assigned (the admin centre's Licences figures), how each licence is held — directly or through a group — " +
        "and licensed accounts that are blocked from signing in or have not signed in for 90 days.";

    public override IReadOnlyList<ChoiceItem> Shows { get; } =
    [
        new("skus", "Subscriptions"),
        new("flagged", "Licensed but unused"),
        new("disabled", "Licensed, sign-in blocked"),
        new("quiet", "Licensed, quiet 90 days"),
    ];

    protected override Capability? RequiredCapability => null;

    protected override DateTimeOffset TakenAtOf(LicenceReport report) => report.TakenAt;

    protected override Task<LicenceReport> BuildCoreAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Reading subscriptions and every account's licences…");
        return _service.BuildAsync(ct);
    }

    protected override IEnumerable<ReportFigure> FiguresOf(LicenceReport r)
    {
        // Seats and assigned agree only over active subscriptions: a suspended one has no seats but keeps its holders.
        var paid = r.Skus.Where(s => !s.LooksFree && !s.IsSuspended).ToList();
        var lapsed = r.Skus.Where(s => s.IsSuspended).Sum(s => s.Consumed);
        return
        [
            new("Paid seats", paid.Sum(s => s.Enabled + s.Warning).ToString("N0", CultureInfo.CurrentCulture)),
            new("Assigned", paid.Sum(s => s.Consumed).ToString("N0", CultureInfo.CurrentCulture)),
            new("Unassigned", paid.Sum(s => s.Available).ToString("N0", CultureInfo.CurrentCulture)),
            new("Assigned on suspended subscriptions", lapsed.ToString("N0", CultureInfo.CurrentCulture), lapsed > 0),
            new("Blocked but licensed", r.Flagged.Count(f => !f.Enabled).ToString("N0", CultureInfo.CurrentCulture), r.Flagged.Any(f => !f.Enabled)),
            new($"Quiet {r.QuietDays} days", r.QuietUnavailable is null ? r.Flagged.Count(f => f.IsQuiet).ToString("N0", CultureInfo.CurrentCulture) : "—", r.Flagged.Any(f => f.IsQuiet)),
        ];
    }

    protected override string? NoteOf(LicenceReport r) => r.QuietUnavailable;

    protected override IEnumerable<object> RowsOf(LicenceReport r, string show) => show switch
    {
        "skus" => r.Skus,
        "disabled" => r.Flagged.Where(f => !f.Enabled),
        "quiet" => r.Flagged.Where(f => f.IsQuiet),
        _ => r.Flagged,
    };

    protected override IReadOnlyList<string> ExportHeaders =>
        Show.Value == "skus"
            ? ["Subscription", "Part number", "Seats", "Assigned", "Unassigned", "Direct", "Through a group", "Blocked but licensed", "Quiet", "Suspended seats"]
            : ["Name", "Sign-in name", "Reason", "Licences", "Last sign-in", "Sign-in allowed"];

    protected override IReadOnlyList<string?> ExportCells(object row) => row switch
    {
        LicenceSkuRow s => [s.Name, s.PartNumber, N(s.Enabled + s.Warning), N(s.Consumed), N(s.Available), N(s.Direct), N(s.ViaGroup), N(s.Disabled), N(s.Quiet), N(s.Suspended)],
        LicensedUserRow u => [u.DisplayName, u.Upn, u.Reason, u.LicencesText, u.LastSignIn?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), u.Enabled ? "Yes" : "No"],
        _ => [],
    };

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
