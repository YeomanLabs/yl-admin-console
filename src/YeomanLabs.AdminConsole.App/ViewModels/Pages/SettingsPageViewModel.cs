using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.App.ViewModels.Dialogs;
using YeomanLabs.AdminConsole.Core;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Sync;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Settings (UX §12): Account, Sync, Appearance, Permissions, Diagnostics. Changing an interval or the theme takes
/// effect immediately and is remembered in <c>settings.json</c> beside the cache.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject, IPageViewModel
{
    private readonly ConsentService _consent;
    private readonly PermissionChecker _checker;
    private readonly TenantContext _tenant;
    private readonly ToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly UserSettingsStore _settingsStore;
    private readonly SyncOptions _syncOptions;
    private readonly IServiceProvider _services;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private bool _loadingSettings;

    public SettingsPageViewModel(
        ConsentService consent,
        PermissionChecker checker,
        TenantContext tenant,
        ToastService toasts,
        IDialogService dialogs,
        UserSettingsStore settingsStore,
        SyncOptions syncOptions,
        IServiceProvider services,
        ILogger<SettingsPageViewModel> logger)
    {
        _consent = consent;
        _checker = checker;
        _tenant = tenant;
        _toasts = toasts;
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _syncOptions = syncOptions;
        _services = services;
        _logger = logger;
        LoadSettings();

        Permissions = Capabilities.All.Select(c => new PermissionRow(c, _checker.Check(c), GrantAsync)).ToList();
        _tenant.Changed += (_, _) => RefreshPermissions();
        Refresh();
    }

    public string Title => "Settings";

    // ---- Account (UX §12) -------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _accountName = string.Empty;

    [ObservableProperty]
    private string _tenantName = string.Empty;

    [ObservableProperty]
    private string _tenantId = string.Empty;

    [ObservableProperty]
    private string _rolesText = string.Empty;

    [ObservableProperty]
    private string _licenseText = string.Empty;

    // ---- Permissions (UX §12) ---------------------------------------------------------------------------------------

    public IReadOnlyList<PermissionRow> Permissions { get; }

    [ObservableProperty]
    private string _grantedSummary = string.Empty;

    [ObservableProperty]
    private string? _consentError;

    /// <summary>Consents to the whole write tier in one prompt rather than one per capability.</summary>
    [RelayCommand]
    private async Task GrantAllWriteAsync()
    {
        ConsentError = null;
        try
        {
            await _consent.EnsureWriteScopesAsync(CancellationToken.None);
            _toasts.Show("Write permissions granted");
        }
        catch (SignInException ex)
        {
            _logger.LogInformation("Write consent not completed: {Kind}", ex.Kind);
            ConsentError = Describe(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Write consent failed");
            ConsentError = ex.Message;
        }
    }

    private async Task GrantAsync(Capability capability)
    {
        ConsentError = null;
        try
        {
            var result = await _consent.EnsureAsync(capability, CancellationToken.None);
            if (result.CanRun) _toasts.Show($"Granted: {capability.Title}");
        }
        catch (SignInException ex)
        {
            _logger.LogInformation("Consent for {Capability} not completed: {Kind}", capability.Id, ex.Kind);
            ConsentError = Describe(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consent for {Capability} failed", capability.Id);
            ConsentError = ex.Message;
        }
    }

    private static string Describe(SignInException ex) => ex.Kind switch
    {
        SignInFailureKind.Cancelled => "Consent was cancelled. Nothing changed.",
        SignInFailureKind.ConsentDeclined => "Consent was declined. The permission was not granted.",
        SignInFailureKind.ConditionalAccess => ex.RawMessage ?? ex.Message,
        _ => ex.Message,
    };

    // ---- Sync (UX §12) ----------------------------------------------------------------------------------------------

    public IReadOnlyList<int> DeltaChoices => UserSettings.DeltaChoices;
    public IReadOnlyList<int> DeviceRefreshChoices => UserSettings.DeviceRefreshChoices;

    [ObservableProperty]
    private int _deltaIntervalSeconds;

    [ObservableProperty]
    private int _deviceRefreshIntervalSeconds;

    partial void OnDeltaIntervalSecondsChanged(int value) => ApplyAndSave();

    partial void OnDeviceRefreshIntervalSecondsChanged(int value) => ApplyAndSave();

    [ObservableProperty]
    private bool _isRebuilding;

    /// <summary>
    /// Throws away every delta link and cached row so the next cycle walks the tenant from scratch. The remedy when
    /// the cache and the directory have drifted apart and nobody can say why.
    /// </summary>
    [RelayCommand]
    private async Task RebuildIndexAsync()
    {
        if (!_tenant.IsLoaded) return;

        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = "Rebuild the local index?",
            Message = "Everything cached about this tenant is discarded and read again from Graph. Nothing in the tenant changes.",
            Bullets = ["Search is incomplete until the walk finishes", "On a large tenant this takes a few minutes"],
            ConfirmLabel = "Rebuild",
        });

        if (!confirmed) return;

        IsRebuilding = true;
        try
        {
            var db = _services.GetRequiredService<CacheDb>();
            await Task.Run(() =>
            {
                foreach (var entity in Enum.GetValues<CacheEntity>()) db.Clear(entity);
            });

            _services.GetRequiredService<DeltaSyncService>().RequestSync();
            _toasts.Show("Local index cleared — rebuilding now");
            _logger.LogInformation("Local index cleared by request");
            RefreshDiagnostics();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rebuilding the index failed");
            _toasts.Show($"Could not rebuild the index: {ex.Message}");
        }
        finally
        {
            IsRebuilding = false;
        }
    }

    // ---- Appearance (UX §12) ----------------------------------------------------------------------------------------

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();
    public IReadOnlyList<AppDensity> Densities { get; } = Enum.GetValues<AppDensity>();

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private AppDensity _density;

    partial void OnThemeChanged(AppTheme value)
    {
        AppearanceService.Apply(value);
        ApplyAndSave();
    }

    partial void OnDensityChanged(AppDensity value)
    {
        AppearanceService.Apply(value);
        ApplyAndSave();
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        var settings = _settingsStore.Load();
        DeltaIntervalSeconds = Nearest(settings.DeltaIntervalSeconds, UserSettings.DeltaChoices);
        DeviceRefreshIntervalSeconds = Nearest(settings.DeviceRefreshIntervalSeconds, UserSettings.DeviceRefreshChoices);
        Theme = settings.Theme;
        Density = settings.Density;
        _loadingSettings = false;
    }

    /// <summary>A hand-edited file may hold any number; the dropdown still has to select something.</summary>
    private static int Nearest(int value, IReadOnlyList<int> choices) =>
        choices.Contains(value) ? value : choices.OrderBy(c => Math.Abs(c - value)).First();

    private void ApplyAndSave()
    {
        if (_loadingSettings) return;

        // The running sync loop reads these each cycle, so changing one takes effect without a restart.
        _syncOptions.DeltaIntervalSeconds = DeltaIntervalSeconds;
        _syncOptions.DeviceRefreshIntervalSeconds = DeviceRefreshIntervalSeconds;

        _settingsStore.Save(new UserSettings
        {
            DeltaIntervalSeconds = DeltaIntervalSeconds,
            DeviceRefreshIntervalSeconds = DeviceRefreshIntervalSeconds,
            Theme = Theme,
            Density = Density,
        });
    }

    // ---- Diagnostics (UX §12) ---------------------------------------------------------------------------------------

    public string LogsPath => AppPaths.LogsDirectory;
    public string CachePath => _tenant.IsLoaded ? AppPaths.CacheDbPath(_tenant.TenantId) : AppPaths.CacheDirectory;
    public string ActionsPath => AppPaths.ActionsDbPath;

    [ObservableProperty]
    private string _cacheSizeText = "—";

    [RelayCommand]
    private void OpenLogsFolder() => Reveal(AppPaths.LogsDirectory);

    [RelayCommand]
    private void OpenCacheFolder() => Reveal(AppPaths.CacheDirectory);

    private void Reveal(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open {Path}", path);
            _toasts.Show("Could not open that folder");
        }
    }

    /// <summary>
    /// Zips the log folder next to it. Logs hold object ids and error text but never tokens or passwords, so this is
    /// safe to hand to someone; it still says what is inside before it is shared.
    /// </summary>
    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var target = Path.Combine(AppPaths.Root, $"logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await Task.Run(() =>
            {
                // Copy first: the current log file is open for writing, and ZipFile cannot read it in place.
                var staging = Path.Combine(Path.GetTempPath(), $"yl-logs-{Guid.NewGuid():N}");
                Directory.CreateDirectory(staging);
                try
                {
                    foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDirectory, "*.log"))
                    {
                        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var copy = File.Create(Path.Combine(staging, Path.GetFileName(file)));
                        source.CopyTo(copy);
                    }

                    if (File.Exists(target)) File.Delete(target);
                    ZipFile.CreateFromDirectory(staging, target);
                }
                finally
                {
                    Directory.Delete(staging, recursive: true);
                }
            });

            ClipboardText.TryCopy(target);
            _toasts.Show($"Logs exported to {Path.GetFileName(target)} — path copied");
            _logger.LogInformation("Exported logs to {Target}", target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting logs failed");
            _toasts.Show($"Could not export the logs: {ex.Message}");
        }
    }

    private void RefreshDiagnostics()
    {
        OnPropertyChanged(nameof(CachePath));
        try
        {
            var path = _tenant.IsLoaded ? AppPaths.CacheDbPath(_tenant.TenantId) : null;
            CacheSizeText = path is not null && File.Exists(path)
                ? $"{new FileInfo(path).Length / 1024d / 1024d:F1} MB"
                : "—";
        }
        catch (IOException)
        {
            CacheSizeText = "—";
        }
    }

    /// <summary>Signs out and clears the token cache (UX §12 Account). The app returns to its sign-in page.</summary>
    [RelayCommand]
    private async Task SignOutAsync()
    {
        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = "Sign out?",
            Message = "The saved token is removed from this machine. The cached directory data stays until you rebuild the index.",
            ConfirmLabel = "Sign out",
            Destructive = true,
        });

        if (!confirmed) return;

        try
        {
            await _services.GetRequiredService<IAuthService>().SignOutAsync(CancellationToken.None);
            _logger.LogInformation("Signed out by request");
            SignedOut?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-out failed");
            _toasts.Show($"Could not sign out: {ex.Message}");
        }
    }

    /// <summary>Set by the shell: returns the window to the sign-in page.</summary>
    public Action? SignedOut { get; set; }

    private void Refresh()
    {
        RefreshDiagnostics();
        AccountName = _tenant.SignedInUpn;
        TenantName = _tenant.TenantDisplayName;
        TenantId = _tenant.TenantId;
        RolesText = _tenant.Roles.Count == 0 ? "No directory roles" : string.Join(", ", _tenant.Roles.Select(r => r.DisplayName));
        LicenseText = _tenant.HasP2 ? "Microsoft Entra ID P2" : _tenant.HasP1 ? "Microsoft Entra ID P1" : "No Entra ID Premium — sign-in activity is unavailable";
        RefreshPermissions();
    }

    private void RefreshPermissions()
    {
        foreach (var row in Permissions) row.Apply(_checker.Check(row.Capability));
        var granted = Permissions.Count(p => p.IsGranted || p.HasRoleWarning);
        GrantedSummary = $"{granted} of {Permissions.Count} capabilities available · {_tenant.GrantedScopes.Count} scopes consented";
    }
}
