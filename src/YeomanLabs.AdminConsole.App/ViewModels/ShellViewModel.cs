using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;
using YeomanLabs.AdminConsole.App.ViewModels.Pages;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Sync;

namespace YeomanLabs.AdminConsole.App.ViewModels;

/// <summary>
/// Root of the UI (UX §2 shell): title bar state, nav rail, content region, status bar, and the non-modal error bar.
/// One instance per process; page VMs are transient and created through DI when navigated to.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly TenantContext _tenant;
    private readonly ILogger<ShellViewModel> _logger;
    private ISyncStatus? _sync;

    public ShellViewModel(IServiceProvider services, TenantContext tenant, ILogger<ShellViewModel> logger)
    {
        _services = services;
        _tenant = tenant;
        _logger = logger;

        NavItems =
        [
            new NavItem("home", "Home", SymbolRegular.Home24, 1, typeof(HomePageViewModel)),
            new NavItem("users", "Users", SymbolRegular.People24, 2, typeof(PlaceholderPageViewModel)),
            new NavItem("groups", "Groups", SymbolRegular.PeopleTeam24, 3, typeof(PlaceholderPageViewModel)),
            new NavItem("devices", "Devices", SymbolRegular.Laptop24, 4, typeof(PlaceholderPageViewModel)),
            new NavItem("templates", "Templates", SymbolRegular.DocumentBulletList24, 5, typeof(PlaceholderPageViewModel)),
            new NavItem("activity", "Activity log", SymbolRegular.History24, 6, typeof(PlaceholderPageViewModel)),
        ];
        SettingsItem = new NavItem("settings", "Settings", SymbolRegular.Settings24, 0, typeof(PlaceholderPageViewModel));

        _tenant.Changed += (_, _) => RunOnUi(RefreshTenantHeader);
        Navigate(NavItems[0]);
    }

    // ---- title bar ---------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _tenantName = "Not signed in";

    [ObservableProperty]
    private string _accountName = string.Empty;

    [ObservableProperty]
    private string _accountInitials = "?";

    [ObservableProperty]
    private SyncPillState _syncState = SyncPillState.Offline;

    [ObservableProperty]
    private string _syncStatusText = "Offline";

    // ---- nav rail ----------------------------------------------------------------------------------------------

    public IReadOnlyList<NavItem> NavItems { get; }
    public NavItem SettingsItem { get; }

    [ObservableProperty]
    private NavItem? _selectedNav;

    [ObservableProperty]
    private object? _currentPage;

    [RelayCommand]
    private void Navigate(NavItem item)
    {
        foreach (var n in NavItems) n.IsSelected = ReferenceEquals(n, item);
        SettingsItem.IsSelected = ReferenceEquals(SettingsItem, item);
        SelectedNav = item;
        CurrentPage = CreatePage(item);
        _logger.LogDebug("Navigated to {Nav}", item.Id);
    }

    /// <summary>Alt+1..6 (UX §11).</summary>
    [RelayCommand]
    private void NavigateOrdinal(string ordinal)
    {
        if (int.TryParse(ordinal, out var n) && NavItems.FirstOrDefault(i => i.Ordinal == n) is { } item) Navigate(item);
    }

    private object CreatePage(NavItem item)
    {
        var page = _services.GetRequiredService(item.PageViewModelType);
        if (page is PlaceholderPageViewModel placeholder)
        {
            placeholder.Title = item.Title;
            placeholder.ArrivesIn = item.Id switch
            {
                "users" or "groups" or "devices" => "Object lists arrive with User 360 (M4).",
                "templates" => "User templates arrive in M7.",
                "activity" => "The activity log page arrives in M8.",
                "settings" => "Settings arrive in M5/M8.",
                _ => string.Empty,
            };
        }

        return page;
    }

    // ---- status bar (UX §2: cache age · last delta · errors · progress) ------------------------------------------

    [ObservableProperty]
    private string _cacheAgeText = "No cache";

    [ObservableProperty]
    private string _lastDeltaText = string.Empty;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressVisible))]
    private double _progressFraction = -1;

    public bool IsProgressVisible => ProgressFraction >= 0;

    /// <summary>Called once after sign-in (M3.3) when the per-tenant services exist. Wires the sync pill and status bar.</summary>
    public void AttachSession()
    {
        RefreshTenantHeader();
        _sync = _services.GetRequiredService<ISyncStatus>();
        _sync.ProgressChanged += (_, p) => RunOnUi(() => OnSyncProgress(p));
        SyncState = SyncPillState.Syncing;
        SyncStatusText = "Preparing…";
    }

    private void RefreshTenantHeader()
    {
        if (!_tenant.IsLoaded) return;
        TenantName = _tenant.TenantDisplayName;
        AccountName = _tenant.SignedInUpn;
        AccountInitials = Initials(_tenant.SignedInDisplayName, _tenant.SignedInUpn);
    }

    private void OnSyncProgress(SyncProgress p)
    {
        if (p.Error is not null)
        {
            SyncState = SyncPillState.Error;
            SyncStatusText = $"{Label(p.Entity)}: sync failed";
            ProgressFraction = -1;
            ProgressText = string.Empty;
            return;
        }

        if (!p.IsDone)
        {
            SyncState = SyncPillState.Syncing;
            SyncStatusText = p.Phase == SyncPhase.Initial ? "Building index…" : "Syncing…";
            ProgressText = p.Total is { } t ? $"{Label(p.Entity)} {p.Processed:N0} / {t:N0}" : $"{Label(p.Entity)} {p.Processed:N0}";
            ProgressFraction = p.Fraction ?? 0;
            return;
        }

        ProgressText = string.Empty;
        ProgressFraction = -1;
        if (_sync is { IsRunning: false })
        {
            SyncState = _sync.LastError is null ? SyncPillState.Ok : SyncPillState.Error;
            SyncStatusText = _sync.LastError is null ? "Synced" : "Sync errors";
            var now = DateTimeOffset.UtcNow;
            CacheAgeText = $"Cache {Age(_sync.LastCycleCompleted, now)}";
            LastDeltaText = $"Last delta {Age(_sync.LastCycleCompleted, now)}";
        }
    }

    private static string Label(CacheEntity e) => e switch
    {
        CacheEntity.Users => "users",
        CacheEntity.Groups => "groups",
        CacheEntity.Devices => "devices",
        CacheEntity.ManagedDevices => "Intune devices",
        CacheEntity.Skus => "licenses",
        CacheEntity.Organization => "organization",
        _ => e.ToString(),
    };

    private static string Age(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null) return "never";
        var age = now - at.Value;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
    }

    private static string Initials(string displayName, string upn)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
        if (parts.Length == 1) return char.ToUpperInvariant(parts[0][0]).ToString();
        return upn.Length > 0 ? char.ToUpperInvariant(upn[0]).ToString() : "?";
    }

    // ---- error bar (CLAUDE.md non-negotiable 2: human-readable message plus the raw error behind an expander) -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorTitle;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetails))]
    private string? _errorDetails;

    public bool HasError => ErrorTitle is not null;
    public bool HasErrorDetails => !string.IsNullOrEmpty(ErrorDetails);

    /// <summary>Shows the error bar. Safe from any thread.</summary>
    public void ShowError(string title, string message, string? details = null)
    {
        RunOnUi(() =>
        {
            ErrorTitle = title;
            ErrorMessage = message;
            ErrorDetails = details;
        });
    }

    /// <summary>Graph errors get their code and request-id; everything else the type and message.</summary>
    public void ShowError(Exception ex, string? context = null)
    {
        var title = context ?? "Something went wrong";
        if (ex is GraphException g)
        {
            var details = $"HTTP {g.StatusCode} {g.ErrorCode}\nrequest-id: {g.RequestId ?? "n/a"}\nclient-request-id: {g.ClientRequestId ?? "n/a"}\n{g.Timestamp:u}\n\n{g.RawBody}";
            ShowError(title, g.Message, details);
            return;
        }

        ShowError(title, ex.Message, ex.ToString());
    }

    [RelayCommand]
    private void DismissError()
    {
        ErrorTitle = null;
        ErrorMessage = null;
        ErrorDetails = null;
    }

    [RelayCommand]
    private void CopyErrorDetails()
    {
        try
        {
            Clipboard.SetText($"{ErrorTitle}\n{ErrorMessage}\n\n{ErrorDetails}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard unavailable");
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
