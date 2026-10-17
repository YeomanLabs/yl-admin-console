using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// The Activity page (M8.2): every write this admin has made, from <c>actions.db</c>. Filters narrow it, and the whole
/// filtered view copies as markdown — which is what an admin actually needs when a change has to be explained to
/// somebody else.
/// </summary>
public sealed partial class ActivityPageViewModel : ObservableObject, IPageViewModel, IRefreshable
{
    private const int PageSize = 300;

    private readonly ActionLog _actions;
    private readonly ToastService _toasts;
    private readonly ILogger<ActivityPageViewModel> _logger;

    private readonly AdminConsole.Core.Auth.TenantContext _tenant;

    public ActivityPageViewModel(ActionLog actions, AdminConsole.Core.Auth.TenantContext tenant, ToastService toasts, ILogger<ActivityPageViewModel> logger)
    {
        _actions = actions;
        _tenant = tenant;
        _toasts = toasts;
        _logger = logger;
        Reload();
    }

    public string Title => "Activity log";

    /// <summary>Set by the shell: opens the object a row acted on.</summary>
    public Action<ObjectType, string, string>? OpenObject { get; set; }

    // ---- filters ----------------------------------------------------------------------------------------------------

    public IReadOnlyList<string> Kinds { get; } = ["Everything", "Users", "Groups", "Devices", "Licences"];

    public IReadOnlyList<string> Outcomes { get; } = ["Any outcome", "Success", "Failed", "Skipped"];

    public IReadOnlyList<string> Ranges { get; } = ["Today", "Last 7 days", "Last 30 days", "Everything"];

    [ObservableProperty]
    private string _kind = "Everything";

    [ObservableProperty]
    private string _outcome = "Any outcome";

    [ObservableProperty]
    private string _range = "Last 7 days";

    [ObservableProperty]
    private string _search = string.Empty;

    partial void OnKindChanged(string value) => Reload();
    partial void OnOutcomeChanged(string value) => Reload();
    partial void OnRangeChanged(string value) => Reload();
    partial void OnSearchChanged(string value) => Reload();

    [RelayCommand]
    private void ClearFilters()
    {
        Kind = "Everything";
        Outcome = "Any outcome";
        Range = "Last 7 days";
        Search = string.Empty;
    }

    // ---- rows -------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private IReadOnlyList<ActivityRow> _rows = [];

    public bool HasRows => Rows.Count > 0;

    public string CountText => Rows.Count switch
    {
        0 => "Nothing matches these filters",
        1 => "1 action",
        var n when n >= PageSize => $"{n} actions (the most recent {PageSize})",
        var n => $"{n} actions",
    };

    public Task RefreshAsync(CancellationToken ct)
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        var query = new ActionLogQuery
        {
            Limit = PageSize,
            TargetType = Kind switch
            {
                "Users" => ObjectType.User,
                "Groups" => ObjectType.Group,
                "Devices" => ObjectType.Device,
                _ => null,
            },
            ActionPrefix = Kind == "Licences" ? "license." : null,
            // M20: this tab's tenant only; the log itself is shared by every tenant the installation has opened.
            TenantId = _tenant.IsLoaded ? _tenant.TenantId : null,
            Outcome = Outcome switch
            {
                "Success" => ActionOutcome.Success,
                "Failed" => ActionOutcome.Failed,
                "Skipped" => ActionOutcome.Skipped,
                _ => null,
            },
            Since = Range switch
            {
                "Today" => DateTimeOffset.Now.Date,
                "Last 7 days" => DateTimeOffset.UtcNow.AddDays(-7),
                "Last 30 days" => DateTimeOffset.UtcNow.AddDays(-30),
                _ => null,
            },
        };

        var rows = _actions.Query(query).Select(r => new ActivityRow(r, Open)).ToList();

        // The free-text box filters what came back rather than the query: the action log has no index for it, and a
        // few hundred rows in memory is faster than teaching SQLite to do it.
        if (Search.Trim() is { Length: > 0 } text)
        {
            rows = rows.Where(r => r.Matches(text)).ToList();
        }

        Rows = rows;
    }

    private void Open(ActivityRow row) => OpenObject?.Invoke(row.Record.TargetType, row.Record.TargetId, row.TargetName);

    // ---- copy as markdown -------------------------------------------------------------------------------------------

    [RelayCommand]
    private void CopyAsMarkdown()
    {
        if (Rows.Count == 0) return;
        _toasts.Show(ClipboardText.TryCopy(BuildMarkdown()) ? $"Copied {Rows.Count} action(s) as markdown" : "The clipboard is unavailable right now");
        _logger.LogInformation("Copied {Count} action log row(s) as markdown", Rows.Count);
    }

    internal string BuildMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Activity log");
        sb.AppendLine();
        sb.AppendLine($"- Filters: {Kind} · {Outcome} · {Range}{(Search.Trim().Length == 0 ? string.Empty : $" · \"{Search.Trim()}\"")}");
        sb.AppendLine($"- Rows: {Rows.Count}");
        sb.AppendLine();
        sb.AppendLine("| When | Action | Target | Outcome | Result |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var row in Rows)
        {
            sb.AppendLine($"| {row.Record.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} | {row.Record.Action} | {Escape(row.TargetName)} | {row.Record.Outcome} | {Escape(row.Record.Message ?? row.Record.RequestSummary ?? string.Empty)} |");
        }

        return sb.ToString();
    }

    /// <summary>A pipe in a display name would break the table it lands in.</summary>
    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}

/// <summary>One row of the activity log.</summary>
public sealed partial class ActivityRow : ObservableObject
{
    private readonly Action<ActivityRow> _open;

    public ActivityRow(ActionRecord record, Action<ActivityRow> open)
    {
        Record = record;
        _open = open;
    }

    public ActionRecord Record { get; }

    public string TargetName => Record.TargetName ?? Record.TargetId;
    public string WhenText => TimeText.Both(Record.Timestamp);
    public string ActionText => Record.Action;
    public string DetailText => Record.Message ?? Record.RequestSummary ?? string.Empty;
    public string OutcomeText => Record.Outcome.ToString();

    public bool IsFailed => Record.Outcome == ActionOutcome.Failed;
    public bool WasUndone => Record.UndoneAt is not null;
    public bool HasCorrelation => Record.CorrelationId is not null;

    /// <summary>"part of an offboarding run" — the correlation id groups a run's rows without showing the raw GUID.</summary>
    public string CorrelationText => Record.CorrelationId switch
    {
        null => string.Empty,
        var id when id.StartsWith("offboard-", StringComparison.Ordinal) => "part of an offboarding run",
        var id when id.StartsWith("create-", StringComparison.Ordinal) => "part of a user creation",
        _ => "part of a run",
    };

    public bool Matches(string text) =>
        TargetName.Contains(text, StringComparison.OrdinalIgnoreCase)
        || ActionText.Contains(text, StringComparison.OrdinalIgnoreCase)
        || DetailText.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (Record.Actor?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    [RelayCommand]
    private void Open() => _open(this);
}
