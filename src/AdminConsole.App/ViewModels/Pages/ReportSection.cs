using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Export;
using AdminConsole.Core.Reports;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// What the three reports share (M19): the last saved report shown at once with its date, a rebuild on request, a
/// "show" filter, headline figures, and a CSV export of exactly the rows the filter shows.
/// </summary>
public abstract partial class ReportSection<TReport, TRow> : ObservableObject where TReport : class
{
    private readonly ReportStore _store;
    private readonly TenantContext _tenant;
    private readonly ConsentService _consent;
    private readonly PermissionChecker _permissions;
    private readonly string _kind;
    private readonly ILogger _logger;
    private TReport? _report;

    protected ReportSection(ReportStore store, TenantContext tenant, ConsentService consent, PermissionChecker permissions, string kind, ILogger logger)
    {
        _store = store;
        _tenant = tenant;
        _consent = consent;
        _permissions = permissions;
        _kind = kind;
        _logger = logger;
        _show = Shows[0];
    }

    public abstract string Title { get; }

    /// <summary>What the report answers, in one line, and where its figures come from.</summary>
    public abstract string Description { get; }

    public abstract IReadOnlyList<ChoiceItem> Shows { get; }

    public FilePicker? Files { get; set; }

    public Action<string>? ReportStatus { get; set; }

    public ObservableCollection<ReportFigure> Figures { get; } = [];

    public ObservableCollection<TRow> Rows { get; } = [];

    [ObservableProperty]
    private ChoiceItem _show;

    partial void OnShowChanged(ChoiceItem value) => Refill();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(ExportCommand))]
    private bool _isBuilding;

    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    private string? _error;

    /// <summary>A sentence about the data the report could not get (no P1 for sign-ins), shown under the figures.</summary>
    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport), nameof(BuildText))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _asOfText = "Not built yet. Build it to see the figures; it is kept until you rebuild it.";

    public bool HasReport => _report is not null;

    public string BuildText => HasReport ? "Rebuild" : "Build report";

    public string BuildName => $"Build the {Title.ToLowerInvariant()} report";

    public string ExportName => $"Export the {Title.ToLowerInvariant()} report to CSV";

    [ObservableProperty]
    private string _countText = string.Empty;

    protected abstract Capability? RequiredCapability { get; }

    protected abstract DateTimeOffset TakenAtOf(TReport report);

    protected abstract Task<TReport> BuildCoreAsync(IProgress<string> progress, CancellationToken ct);

    protected abstract IEnumerable<ReportFigure> FiguresOf(TReport report);

    protected abstract string? NoteOf(TReport report);

    protected abstract IEnumerable<TRow> RowsOf(TReport report, string show);

    protected abstract IReadOnlyList<string> ExportHeaders { get; }

    protected abstract IReadOnlyList<string?> ExportCells(TRow row);

    /// <summary>The saved report, read off the UI thread as the page opens.</summary>
    public async Task LoadSavedAsync()
    {
        if (!_tenant.IsLoaded) return;
        var saved = await Task.Run(() => _store.Load<TReport>(_tenant.TenantId, _kind)).ConfigureAwait(true);
        if (saved is not null) Apply(saved);
    }

    private void Apply(TReport report)
    {
        _report = report;
        var at = TakenAtOf(report).ToLocalTime();
        var age = DateTimeOffset.Now - at;
        var ago = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago" : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalDays} days ago";
        AsOfText = $"Built {at.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture)} ({ago}). Rebuild for today's figures.";
        Figures.Clear();
        foreach (var f in FiguresOf(report)) Figures.Add(f);
        Note = NoteOf(report);
        Refill();
    }

    private void Refill()
    {
        Rows.Clear();
        if (_report is null) return;
        foreach (var row in RowsOf(_report, Show.Value)) Rows.Add(row);
        CountText = Rows.Count == 1 ? "1 row" : $"{Rows.Count:N0} rows";
    }

    private bool CanBuild => !IsBuilding;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        Error = null;
        if (RequiredCapability is { } capability && _permissions.Check(capability).NeedsConsent)
        {
            try
            {
                var granted = await _consent.EnsureAsync(capability, CancellationToken.None);
                if (!granted.CanRun) { Error = granted.Reason; return; }
            }
            catch (SignInException)
            {
                Error = "The permission was not granted, so the report was not built.";
                return;
            }
        }

        IsBuilding = true;
        ProgressText = "Reading…";
        try
        {
            var progress = new Progress<string>(text => ProgressText = text);
            var report = await Task.Run(() => BuildCoreAsync(progress, CancellationToken.None)).ConfigureAwait(true);
            await Task.Run(() => _store.Save(_tenant.TenantId, _kind, report)).ConfigureAwait(true);
            Apply(report);
        }
        catch (Core.Graph.GraphException ex)
        {
            _logger.LogWarning("Building the {Kind} report failed: {Status} {Code}", _kind, ex.StatusCode, ex.ErrorCode);
            Error = ex.StatusCode == 403
                ? $"Graph refused the report: {Core.Graph.GraphErrorText.Friendly(ex)} Reports need Global Reader, Reports Reader or a similar role."
                : $"The report could not be built: {Core.Graph.GraphErrorText.Friendly(ex)}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Building the {Kind} report failed", _kind);
            Error = $"The report could not be built: {ex.Message}";
        }
        finally
        {
            IsBuilding = false;
            ProgressText = null;
        }
    }

    private bool CanExport => HasReport && !IsBuilding;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (Files is null || _report is null) return;
        var name = $"{Title}{(Show == Shows[0] ? string.Empty : " - " + Show.Label)} {TakenAtOf(_report).ToLocalTime():yyyy-MM-dd}.csv";
        name = string.Join("_", name.Split(System.IO.Path.GetInvalidFileNameChars()));
        if (Files.SaveAs(name, "CSV (*.csv)|*.csv", $"Export {Title}") is not { } path) return;

        var rows = Rows.ToList();
        try
        {
            await Task.Run(() =>
            {
                using var writer = new System.IO.StreamWriter(path, append: false, CsvExport.Utf8WithBom);
                CsvExport.Write(writer, ExportHeaders, rows.Select(ExportCells));
            }).ConfigureAwait(true);
            ReportStatus?.Invoke($"Exported {rows.Count:N0} rows to {path}");
        }
        catch (Exception ex)
        {
            ReportStatus?.Invoke($"The export failed: {ex.Message}");
        }
    }
}
