using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Reports;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>M19.2 on the Reports page. Rows are subscriptions; the flagged accounts are their own list beneath.</summary>
public sealed partial class LicenceReportSection : ReportSection<LicenceReport, object>
{
    private readonly LicenceReportService _service;

    public LicenceReportSection(LicenceReportService service, ReportStore store, TenantContext tenant, ConsentService consent, PermissionChecker permissions, ILogger<LicenceReportSection> logger)
        : base(store, tenant, consent, permissions, LicenceReportService.Kind, logger) => _service = service;

    public override string Title => "Licences";

    public override string Description =>
        "Seats bought and assigned (the admin centre's Licences figures), how each licence is held — directly or through a group — " +
        "and licensed accounts that are blocked from signing in or have not signed in for 90 days.";

    private const string HoldersPrefix = "sku:";

    // An ObservableCollection: after the fixed views come one per subscription, "Who holds Microsoft 365 E3", made from
    // whichever report is on screen.
    private readonly ObservableCollection<ChoiceItem> _shows =
    [
        new("skus", "Subscriptions"),
        new("flagged", "Licensed but unused"),
        new("disabled", "Licensed, sign-in blocked"),
        new("quiet", "Licensed, quiet 90 days"),
    ];

    public override IReadOnlyList<ChoiceItem> Shows => _shows;

    private IReadOnlyDictionary<string, string> _skuNames = new Dictionary<string, string>();

    protected override void OnReport(LicenceReport report)
    {
        _skuNames = report.Skus.ToDictionary(s => s.SkuId, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var keep = Show;
        foreach (var old in _shows.Where(c => c.Value.StartsWith(HoldersPrefix, StringComparison.Ordinal)).ToList()) _shows.Remove(old);
        foreach (var sku in report.Skus.Where(s => s.Consumed > 0)) _shows.Add(new ChoiceItem(HoldersPrefix + sku.SkuId, $"Who holds {sku.Name}"));
        // A rebuild keeps the holders list that was open, if that subscription is still there.
        if (keep.Value.StartsWith(HoldersPrefix, StringComparison.Ordinal))
        {
            Show = _shows.FirstOrDefault(c => c.Value == keep.Value) ?? _shows[0];
        }
    }

    /// <summary>Click a subscription: its holders, by name, with how each holds it and when they last signed in.</summary>
    [RelayCommand]
    private void ShowHolders(LicenceSkuRow? sku)
    {
        if (sku is not null && _shows.FirstOrDefault(c => c.Value == HoldersPrefix + sku.SkuId) is { } choice) Show = choice;
    }

    protected override string? BackTextFor(ChoiceItem show) =>
        show.Value.StartsWith(HoldersPrefix, StringComparison.Ordinal) ? "All subscriptions" : null;

    protected override (string Id, string Name)? PersonOf(object row) => row switch
    {
        LicensedUserRow u => (u.UserId, u.DisplayName),
        LicenceHolderRow h => (h.User.UserId, h.User.DisplayName),
        _ => null,
    };

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

    protected override string? NoteOf(LicenceReport r)
    {
        var notes = new List<string>();
        if (r.QuietUnavailable is { } quiet) notes.Add(quiet);
        if (r.Holders.Count == 0 && r.Skus.Any(s => s.Consumed > 0))
        {
            notes.Add("Rebuild to list who holds each subscription: this report was built before that was kept.");
        }
        else if (r.Skus.Any(s => s.Consumed > 0))
        {
            notes.Add("Click a subscription to see who holds it.");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    protected override IEnumerable<object> RowsOf(LicenceReport r, string show) => show switch
    {
        _ when show.StartsWith(HoldersPrefix, StringComparison.Ordinal) =>
            r.HoldersOf(show[HoldersPrefix.Length..]).Select(h => new LicenceHolderRow(h, show[HoldersPrefix.Length..])),
        "skus" => r.Skus,
        "disabled" => r.Flagged.Where(f => !f.Enabled),
        "quiet" => r.Flagged.Where(f => f.IsQuiet),
        _ => r.Flagged,
    };

    protected override IReadOnlyList<string> ExportHeaders =>
        Show.Value == "skus"
            ? ["Subscription", "Part number", "Seats", "Assigned", "Unassigned", "Direct", "Through a group", "Blocked but licensed", "Quiet", "Suspended seats"]
            : Show.Value.StartsWith(HoldersPrefix, StringComparison.Ordinal)
                ? ["Name", "Sign-in name", "Subscription", "How it is held", "Sign-in allowed", "Last sign-in", "All licences"]
                : ["Name", "Sign-in name", "Reason", "Licences", "Last sign-in", "Sign-in allowed"];

    protected override IReadOnlyList<string?> ExportCells(object row) => row switch
    {
        LicenceSkuRow s => [s.Name, s.PartNumber, N(s.Enabled + s.Warning), N(s.Consumed), N(s.Available), N(s.Direct), N(s.ViaGroup), N(s.Disabled), N(s.Quiet), N(s.Suspended)],
        LicensedUserRow u => [u.DisplayName, u.Upn, u.Reason, u.LicencesText, u.LastSignIn?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), u.Enabled ? "Yes" : "No"],
        LicenceHolderRow h => [h.User.DisplayName, h.User.Upn, _skuNames.GetValueOrDefault(h.SkuId, h.SkuId), h.HowHeld, h.User.Enabled ? "Yes" : "No",
            h.User.LastSignIn?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), h.User.LicencesText],
        _ => [],
    };

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
