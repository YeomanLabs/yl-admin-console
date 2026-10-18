using System.Globalization;
using Microsoft.Extensions.Logging;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Reports;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>M19.1 on the Reports page.</summary>
public sealed class MfaReportSection : ReportSection<MfaReport, MfaRow>
{
    private readonly MfaReportService _service;

    public MfaReportSection(MfaReportService service, ReportStore store, TenantContext tenant, ConsentService consent, PermissionChecker permissions, ILogger<MfaReportSection> logger)
        : base(store, tenant, consent, permissions, MfaReportService.Kind, logger) => _service = service;

    public override string Title => "MFA readiness";

    public override string Description =>
        "Who can complete multi-factor sign-in, who cannot yet and what they are missing, and who is not expected to. " +
        "From Entra's user registration details — the same source as the portal's Authentication methods page.";

    public override IReadOnlyList<ChoiceItem> Shows { get; } =
    [
        new("needs", "Needs attention"),
        new("all", "Everyone"),
        new("Missing", "No second factor"),
        new("PhoneOnly", "Phone only"),
        new("Ready", "Ready"),
        new("Exempt", "Exempt"),
    ];

    protected override Capability? RequiredCapability => null;

    protected override DateTimeOffset TakenAtOf(MfaReport report) => report.TakenAt;

    protected override Task<MfaReport> BuildCoreAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Reading registration details…");
        return _service.BuildAsync(ct);
    }

    protected override IEnumerable<ReportFigure> FiguresOf(MfaReport r) =>
    [
        new("People", r.Rows.Count.ToString("N0", CultureInfo.CurrentCulture)),
        new("Ready", r.Count(MfaStatus.Ready).ToString("N0", CultureInfo.CurrentCulture)),
        new("Phone only", r.Count(MfaStatus.PhoneOnly).ToString("N0", CultureInfo.CurrentCulture), r.Count(MfaStatus.PhoneOnly) > 0),
        new("No second factor", r.Count(MfaStatus.Missing).ToString("N0", CultureInfo.CurrentCulture), r.Count(MfaStatus.Missing) > 0),
        new("Exempt", r.Count(MfaStatus.Exempt).ToString("N0", CultureInfo.CurrentCulture)),
        new("Admins not ready", r.AdminsNotReady.ToString("N0", CultureInfo.CurrentCulture), r.AdminsNotReady > 0),
        new("MFA capable (portal's figure)", r.MfaCapable.ToString("N0", CultureInfo.CurrentCulture)),
    ];

    protected override string? NoteOf(MfaReport r)
    {
        var notes = new List<string>();
        if (r.BlockedLeftOut > 0)
        {
            notes.Add($"Entra leaves accounts that are blocked from signing in out of this report: {r.BlockedLeftOut:N0} here. They cannot sign in, so they need no MFA.");
        }

        if (r.SourceUpdatedAt is { } at)
        {
            notes.Add($"Entra last refreshed these registration details at {at.ToLocalTime():g}; a method registered after that shows at its next refresh.");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    protected override IEnumerable<MfaRow> RowsOf(MfaReport r, string show) => show switch
    {
        "all" => r.Rows,
        "needs" => r.Rows.Where(x => x.Status is MfaStatus.Missing or MfaStatus.PhoneOnly),
        _ => r.Rows.Where(x => x.Status.ToString() == show),
    };

    protected override (string Id, string Name)? PersonOf(MfaRow row) => (row.UserId, row.DisplayName);

    protected override IReadOnlyList<string> ExportHeaders { get; } = ["Name", "Sign-in name", "Status", "Detail", "Methods", "Admin", "Guest", "Passwordless capable"];

    protected override IReadOnlyList<string?> ExportCells(MfaRow r) =>
        [r.DisplayName, r.Upn, StatusText(r.Status), r.Detail, r.MethodsText, r.IsAdmin ? "Yes" : "No", r.IsGuest ? "Yes" : "No", r.PasswordlessCapable ? "Yes" : "No"];

    public static string StatusText(MfaStatus s) => s switch
    {
        MfaStatus.Ready => "Ready",
        MfaStatus.PhoneOnly => "Phone only",
        MfaStatus.Missing => "No second factor",
        _ => "Exempt",
    };
}
