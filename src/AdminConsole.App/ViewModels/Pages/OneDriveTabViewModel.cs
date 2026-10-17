using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.OneDrive;
using AdminConsole.Core.SharePoint;
using AdminConsole.Core.Writes;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// User 360's OneDrive tab (M16.2, doc 14): where it is and how full, who may share from it, whether it is locked, who
/// administers it, and an export to ZIP. Reads first; every change runs one at a time with a toast and Undo, then a
/// quiet reload that shows what SharePoint now holds.
/// </summary>
public sealed partial class OneDriveTabViewModel : ObservableObject, IDisposable
{
    private readonly OneDriveService _oneDrive;
    private readonly OneDriveExporter _exporter;
    private readonly ConsentService _consent;
    private readonly PermissionChecker _permissions;
    private readonly CacheDb _db;
    private readonly ToastService _toasts;
    private readonly FilePicker _files;
    private readonly ILogger<OneDriveTabViewModel> _logger;
    private string _userId = string.Empty;
    private bool _requested;
    private bool _applying;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _exportCts;
    private OneDriveDetail? _detail;

    public OneDriveTabViewModel(OneDriveService oneDrive, OneDriveExporter exporter, ConsentService consent, PermissionChecker permissions,
        CacheDb db, ToastService toasts, FilePicker files, ILogger<OneDriveTabViewModel> logger)
    {
        _files = files;
        _oneDrive = oneDrive;
        _exporter = exporter;
        _consent = consent;
        _permissions = permissions;
        _db = db;
        _toasts = toasts;
        _logger = logger;
        LockChoices = [.. OneDriveLock.All.Select(s => new LockChoice(s, OneDriveLock.Describe(s)))];
    }

    /// <summary>Set by User 360: the shell's person picker.</summary>
    public Action<string, Action<PersonRef>>? PickPerson { get; set; }

    /// <summary>Set by User 360: the status bar, for export progress that outlives a glance at the tab.</summary>
    public Action<string>? ReportStatus { get; set; }

    // ---- state -----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(ShowDetail))]
    [NotifyPropertyChangedFor(nameof(ShowStateMessage))]
    [NotifyPropertyChangedFor(nameof(ShowGrant))]
    private OneDriveTabState _state = OneDriveTabState.NotLoaded;

    public bool IsLoading => State is OneDriveTabState.NotLoaded or OneDriveTabState.Loading;
    public bool ShowDetail => State == OneDriveTabState.Loaded;
    public bool ShowStateMessage => State is OneDriveTabState.NeedsConsent or OneDriveTabState.NoOneDrive or OneDriveTabState.Failed;
    public bool ShowGrant => State == OneDriveTabState.NeedsConsent;

    [ObservableProperty] private string? _stateTitle;
    [ObservableProperty] private string? _stateMessage;
    [ObservableProperty] private string? _url;
    [ObservableProperty] private string? _usageText;
    [ObservableProperty] private double _usageFraction;
    [ObservableProperty] private string? _lastModifiedText;
    [ObservableProperty] private string? _ceilingText;
    [ObservableProperty] private string? _lockedNote;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;

    public ObservableCollection<SharingChoice> SharingChoices { get; } = [];
    public IReadOnlyList<LockChoice> LockChoices { get; }
    public ObservableCollection<OneDriveAdminRow> Admins { get; } = [];

    [ObservableProperty] private SharingChoice? _selectedSharing;
    [ObservableProperty] private LockChoice? _selectedLock;

    /// <summary>False while locked: SharePoint refuses sharing and admin changes until the OneDrive is unlocked.</summary>
    public bool CanChangeAccess => _detail is { IsLocked: false } && !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanChangeAccess));

    partial void OnSelectedSharingChanged(SharingChoice? value)
    {
        if (_applying || value is null || _detail is null || value.Value == _detail.Sharing) return;
        var od = _detail;
        _ = RunAsync($"Setting sharing to {value.Label.ToLowerInvariant()}…", Capabilities.ManageOneDrive,
            ct => _oneDrive.SetSharingAsync(od, Label, value.Value, ct));
    }

    partial void OnSelectedLockChanged(LockChoice? value)
    {
        if (_applying || value is null || _detail is null || string.Equals(value.Value, _detail.LockState, StringComparison.OrdinalIgnoreCase)) return;
        var od = _detail;
        _ = RunAsync($"Setting the lock to {value.Label.ToLowerInvariant()}…", Capabilities.ManageOneDrive,
            ct => _oneDrive.SetLockStateAsync(od, Label, value.Value, ct));
    }

    private string Label => _db.GetUser(_userId)?.DisplayName ?? _userId;

    // ---- export ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isExporting;

    [ObservableProperty] private string? _exportText;

    public bool CanExport => !IsExporting && _detail is not null && _detail.LockState != OneDriveLock.NoAccess;

    // ---- lifecycle -------------------------------------------------------------------------------------------------

    public void Start(string userId) => _userId = userId;

    public void EnsureLoaded()
    {
        if (_requested || _userId.Length == 0) return;
        _requested = true;
        _ = LoadAsync(quiet: false);
    }

    public void Reset() => _requested = false;

    [RelayCommand]
    private async Task GrantAsync()
    {
        try
        {
            var result = await _consent.EnsureAsync(Capabilities.ManageOneDrive, CancellationToken.None);
            if (result.CanRun)
            {
                _requested = true;
                await LoadAsync(quiet: false);
            }
        }
        catch (SignInException ex)
        {
            StateMessage = ex.Kind == SignInFailureKind.Cancelled ? "Consent was cancelled. Nothing changed." : $"Consent did not complete: {ex.Message}";
        }
    }

    private async Task LoadAsync(bool quiet)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var permission = _permissions.Check(Capabilities.ManageOneDrive);
        if (!_oneDrive.IsAvailable || permission.NeedsConsent)
        {
            State = OneDriveTabState.NeedsConsent;
            StateTitle = "OneDrive needs the SharePoint permission";
            StateMessage = "This installation hasn't been granted SharePoint access yet. Granting it lets the console read and change OneDrive " +
                           "settings as you; what you can actually change is still decided by your SharePoint role.";
            return;
        }

        if (!quiet) State = OneDriveTabState.Loading;
        try
        {
            var od = await _oneDrive.GetAsync(_userId, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (od is null)
            {
                _detail = null;
                State = OneDriveTabState.NoOneDrive;
                StateTitle = "No OneDrive yet";
                StateMessage = "SharePoint creates a person's OneDrive the first time they open it, and doesn't let an admin create one early " +
                               "on their behalf. Nothing here can change until it exists.";
                return;
            }

            Show(od);
            State = OneDriveTabState.Loaded;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SharePointException ex)
        {
            _logger.LogWarning("OneDrive tab {UserId}: {Code}", _userId, ex.ErrorCode);
            State = OneDriveTabState.Failed;
            StateTitle = "Couldn't read this OneDrive";
            StateMessage = SharePointException.Friendly(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OneDrive tab {UserId}: load failed", _userId);
            State = OneDriveTabState.Failed;
            StateTitle = "Couldn't read this OneDrive";
            StateMessage = ex.Message;
        }
    }

    private void Show(OneDriveDetail od)
    {
        _detail = od;
        _applying = true;
        try
        {
            Url = od.Url;
            UsageFraction = od.FractionUsed ?? 0;
            UsageText = od.StorageQuotaMb > 0
                ? $"{Size(od.StorageUsedMb)} of {Size(od.StorageQuotaMb)} used ({UsageFraction:P0})"
                : $"{Size(od.StorageUsedMb)} used";
            LastModifiedText = od.LastContentModified is { } m ? $"Last change to its files: {m.LocalDateTime:d MMM yyyy, HH:mm}" : null;
            CeilingText = $"The tenant allows OneDrive sharing up to \"{OneDriveSharingText.Describe(od.TenantCeiling)}\".";
            LockedNote = od.IsLocked ? "Locked: SharePoint refuses sharing and admin changes until it is unlocked." : null;

            SharingChoices.Clear();
            foreach (var s in OneDriveSharingText.UpTo(od.TenantCeiling)) SharingChoices.Add(new SharingChoice(s, OneDriveSharingText.Describe(s)));
            if (!SharingChoices.Any(c => c.Value == od.Sharing)) SharingChoices.Add(new SharingChoice(od.Sharing, OneDriveSharingText.Describe(od.Sharing)));
            SelectedSharing = SharingChoices.First(c => c.Value == od.Sharing);
            SelectedLock = LockChoices.FirstOrDefault(c => string.Equals(c.Value, od.LockState, StringComparison.OrdinalIgnoreCase)) ?? LockChoices[0];

            Admins.Clear();
            foreach (var a in od.Admins.OrderByDescending(a => string.Equals(a.UserPrincipalName, od.Owner, StringComparison.OrdinalIgnoreCase)).ThenBy(a => a.Label))
            {
                var isOwner = string.Equals(a.UserPrincipalName, od.Owner, StringComparison.OrdinalIgnoreCase);
                Admins.Add(new OneDriveAdminRow(a, isOwner, isOwner ? "The owner stays an admin" : od.IsLocked ? "Unlock the OneDrive first" : null,
                    RemoveAdminAsync));
            }

            OnPropertyChanged(nameof(CanChangeAccess));
            OnPropertyChanged(nameof(CanExport));
        }
        finally
        {
            _applying = false;
        }
    }

    private static string Size(long mb) => mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb:N0} MB";

    // ---- actions ---------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void CopyUrl()
    {
        if (Url is { } u) _toasts.Show(ClipboardText.TryCopy(u) ? "OneDrive address copied" : "The clipboard is unavailable right now.");
    }

    [RelayCommand]
    private void AddAdmin()
    {
        if (_detail is not { } od) return;
        PickPerson?.Invoke("Choose who becomes an admin of this OneDrive", person =>
        {
            if (person.UserPrincipalName is not { } upn)
            {
                _toasts.Show($"{person.Label} has no sign-in name, so SharePoint can't be told who to add.");
                return;
            }

            _ = RunAsync($"Adding {person.Label}…", Capabilities.ManageOneDrive, ct => _oneDrive.SetAdminAsync(od, Label, upn, person.Label, admin: true, ct));
        });
    }

    private Task RemoveAdminAsync(OneDriveAdminRow row)
    {
        if (_detail is not { } od || row.Admin.UserPrincipalName is not { } upn) return Task.CompletedTask;
        return RunAsync($"Removing {row.Admin.Label}…", Capabilities.ManageOneDrive, ct => _oneDrive.SetAdminAsync(od, Label, upn, row.Admin.Label, admin: false, ct));
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_detail is not { } od || IsExporting) return;
        var suggested = $"{(_db.GetUser(_userId)?.UserPrincipalName ?? _userId).Replace('@', '_')} OneDrive {DateTime.Now:yyyy-MM-dd}.zip";
        if (_files.SaveAs(suggested, "ZIP archive (*.zip)|*.zip", "Export OneDrive to") is not { } path) return;

        var permission = _permissions.Check(Capabilities.ExportOneDrive);
        if (permission.NeedsConsent)
        {
            try
            {
                permission = await _consent.EnsureAsync(Capabilities.ExportOneDrive, CancellationToken.None);
            }
            catch (SignInException)
            {
                _toasts.Show("Permission to read the files was not granted, so nothing was exported.");
                return;
            }
        }

        if (!permission.CanRun)
        {
            _toasts.Show(permission.Reason ?? "Not permitted.");
            return;
        }

        IsExporting = true;
        _exportCts = new CancellationTokenSource();
        var progress = new Progress<ExportProgress>(p =>
        {
            ExportText = p.Text;
            ReportStatus?.Invoke($"OneDrive export: {p.Text}");
        });

        try
        {
            var result = await Task.Run(() => _exporter.ExportAsync(od, Label, path, progress, _exportCts.Token));
            ExportText = result.Complete ? $"{result.Summary}. Saved to {path}" : $"{result.Summary}. Run the export again to the same file to retry just those.";
            _toasts.Show(result.Complete ? result.Summary : $"{result.Summary} — see the OneDrive tab");
        }
        catch (OperationCanceledException)
        {
            ExportText = "Export stopped. Run it again to the same file to carry on where it stopped.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OneDrive export of {UserId} failed", _userId);
            ExportText = $"Export failed: {(ex is SharePointException sp ? SharePointException.Friendly(sp) : ex.Message)}";
        }
        finally
        {
            IsExporting = false;
            ReportStatus?.Invoke(ExportText ?? "OneDrive export finished");
            await LoadAsync(quiet: true);
        }
    }

    [RelayCommand]
    private void CancelExport() => _exportCts?.Cancel();

    /// <summary>One write at a time: consent if needed, the write, a toast (with Undo when it can be undone), a quiet reload.</summary>
    private async Task RunAsync(string busy, Capability capability, Func<CancellationToken, Task<WriteOutcome>> write)
    {
        if (IsBusy) return;
        var permission = _permissions.Check(capability);
        if (permission.NeedsConsent)
        {
            try
            {
                permission = await _consent.EnsureAsync(capability, CancellationToken.None);
            }
            catch (SignInException)
            {
                await LoadAsync(quiet: true);
                return;
            }
        }

        IsBusy = true;
        BusyText = busy;
        try
        {
            var outcome = await write(CancellationToken.None);
            if (!outcome.Success) _toasts.Show(outcome.FriendlyError ?? outcome.Summary);
            else if (outcome.CanUndo) _toasts.Show(outcome.Warning is { } w ? $"{outcome.Summary}. {w}" : outcome.Summary, "Undo", async () =>
            {
                var back = await outcome.Undo!(CancellationToken.None);
                _toasts.Show(back.Success ? $"Undone: {outcome.Summary}" : back.FriendlyError ?? back.Summary);
                await LoadAsync(quiet: true);
            });
            else _toasts.Show(outcome.Summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OneDrive write failed");
            _toasts.Show(ex is SharePointException sp ? SharePointException.Friendly(sp) : ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            await LoadAsync(quiet: true);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _exportCts?.Dispose();
    }
}
