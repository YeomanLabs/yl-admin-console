using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Users.BulkEdit;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// M18.3 — the same fields set on many people. Tick the fields, type the values, read the per-person preview (what
/// changes, and whether it is written in Entra ID or in AD), apply, and watch each person's result arrive.
/// </summary>
public sealed partial class BulkEditDialogViewModel : DialogViewModelBase
{
    private readonly BulkEditService _service;
    private readonly IReadOnlyList<string> _ids;
    private readonly PermissionChecker _permissions;
    private readonly ConsentService _consent;
    private BulkPlan? _plan;
    private int _planVersion;

    public BulkEditDialogViewModel(BulkEditService service, IReadOnlyList<string> ids, PermissionChecker permissions, ConsentService consent)
    {
        _service = service;
        _ids = ids;
        _permissions = permissions;
        _consent = consent;
        Fields = [.. BulkField.All.Select(f => new BulkFieldRow(f, Replan))];
        Replan();
    }

    public override string Title => _ids.Count == 1 ? "Edit 1 person" : $"Edit {_ids.Count:N0} people";

    public override bool ShowFooter => false;

    public override double Width => 760;

    public IReadOnlyList<BulkFieldRow> Fields { get; }

    public ObservableCollection<BulkResultRow> Lines { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private string _summary = "Tick the fields to change.";

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(CloseText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(CloseText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasRun;

    public bool IsEditing => !IsRunning && !HasRun;

    public string CloseText => HasRun ? "Close" : "Cancel";

    public string ApplyText => _plan is { WithWork: > 0 } p ? $"Apply to {p.WithWork:N0} {(p.WithWork == 1 ? "person" : "people")}" : "Apply";

    private async void Replan()
    {
        if (IsRunning || HasRun) return;
        var version = ++_planVersion;
        var changes = Fields.Where(f => f.IsOn).Select(f => new BulkChange(f.Field.Property, f.Value)).ToList();
        if (changes.Count == 0)
        {
            _plan = null;
            Lines.Clear();
            Problem = null;
            Summary = "Tick the fields to change.";
            OnPropertyChanged(nameof(ApplyText));
            return;
        }

        // The cache read is quick but not free on a few hundred people; off the UI thread like every other read.
        var plan = await Task.Run(() => _service.Plan(_ids, changes)).ConfigureAwait(true);
        if (version != _planVersion) return;

        _plan = plan;
        Problem = plan.Problems.Count == 0 ? null : string.Join(" ", plan.Problems);
        Summary = plan.Summary;
        Lines.Clear();
        foreach (var row in plan.Rows) Lines.Add(BulkResultRow.From(row));
        OnPropertyChanged(nameof(ApplyText));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply => !IsRunning && !HasRun && _plan is { WithWork: > 0, Problems.Count: 0 };

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_plan is not { } plan) return;

        var permission = _permissions.Check(Capabilities.EditUser);
        if (permission.NeedsConsent)
        {
            try
            {
                permission = await _consent.EnsureAsync(Capabilities.EditUser, CancellationToken.None);
            }
            catch (SignInException)
            {
                Problem = "Permission was not granted, so nothing was changed.";
                return;
            }

            if (!permission.CanRun)
            {
                Problem = permission.Reason ?? "Permission was not granted, so nothing was changed.";
                return;
            }
        }

        IsRunning = true;
        var correlation = Guid.NewGuid().ToString("N");
        Lines.Clear();
        var progress = new Progress<BulkUserResult>(r => Lines.Add(BulkResultRow.From(r)));
        try
        {
            var results = await Task.Run(() => _service.RunAsync(plan, correlation, progress, CancellationToken.None)).ConfigureAwait(true);
            var failed = results.Count(r => !r.Succeeded);
            var changed = results.Count(r => r.Status == BulkStatus.Changed);
            Summary = failed == 0
                ? $"Done: {changed:N0} changed, {results.Count - changed:N0} already set. One entry in Activity per person, run {correlation[..8]}."
                : $"{failed:N0} of {results.Count:N0} did not fully apply — the lines marked Failed say why. Run {correlation[..8]} in Activity.";
            Problem = null;
        }
        catch (Exception ex)
        {
            Problem = $"The edit stopped: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            HasRun = true;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsRunning) CancelCommand.Execute(null);
    }
}
