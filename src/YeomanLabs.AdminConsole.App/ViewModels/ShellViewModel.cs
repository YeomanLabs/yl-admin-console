using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.App.ViewModels.Pages;
using YeomanLabs.AdminConsole.App.ViewModels.Palette;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Sync;

namespace YeomanLabs.AdminConsole.App.ViewModels;

/// <summary>
/// Root of the UI (UX §2 shell): title bar state, nav rail, tabs + content region with per-tab back stacks, status bar,
/// the non-modal error bar, the first-run flow (UX §9) and the keyboard table (UX §11). One instance per process.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IAuthService _auth;
    private readonly TenantContext _tenant;
    private readonly TenantContextLoader _tenantLoader;
    private readonly ILogger<ShellViewModel> _logger;
    private ISyncStatus? _sync;
    private SignInPageViewModel? _signInPage;
    private bool _scopesExplained;
    private int _statusMessageVersion;

    public ShellViewModel(IServiceProvider services, IAuthService auth, TenantContext tenant, TenantContextLoader tenantLoader, ILogger<ShellViewModel> logger)
    {
        _services = services;
        _auth = auth;
        _tenant = tenant;
        _tenantLoader = tenantLoader;
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

        Tabs.CollectionChanged += OnTabsChanged;
        _tenant.Changed += (_, _) => RunOnUi(RefreshTenantHeader);
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

    /// <summary>False during the first-run flow: the rail is hidden and shortcuts are inert until the tenant is ready.</summary>
    [ObservableProperty]
    private bool _isSessionReady;

    /// <summary>What the content region shows: a first-run page before the session is ready, then the selected tab's page.</summary>
    [ObservableProperty]
    private object? _currentPage;

    [RelayCommand]
    private void Navigate(NavItem item)
    {
        if (!IsSessionReady) return;
        var tab = SelectedTab ?? OpenTab();
        tab.Navigate(CreatePage(item), item);
        ShowTab(tab);
        _logger.LogDebug("Navigated to {Nav}", item.Id);
    }

    /// <summary>Alt+1..6 (UX §11).</summary>
    [RelayCommand]
    private void NavigateOrdinal(string ordinal)
    {
        if (int.TryParse(ordinal, out var n) && NavItems.FirstOrDefault(i => i.Ordinal == n) is { } item) Navigate(item);
    }

    /// <summary>Ctrl+, (UX §11).</summary>
    [RelayCommand]
    private void OpenSettings() => Navigate(SettingsItem);

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

    private void HighlightRail(NavItem? item)
    {
        foreach (var n in NavItems) n.IsSelected = ReferenceEquals(n, item);
        SettingsItem.IsSelected = ReferenceEquals(SettingsItem, item);
    }

    // ---- tabs + back stack (UX §11: Ctrl+T / Ctrl+W / Ctrl+Tab / Alt+←) ---------------------------------------------

    public ObservableCollection<WorkspaceTab> Tabs { get; } = [];

    [ObservableProperty]
    private WorkspaceTab? _selectedTab;

    [ObservableProperty]
    private bool _hasMultipleTabs;

    partial void OnSelectedTabChanged(WorkspaceTab? value)
    {
        foreach (var t in Tabs) t.IsSelected = ReferenceEquals(t, value);
        if (value is null) return;
        CurrentPage = value.CurrentPage;
        HighlightRail(value.Nav);
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => HasMultipleTabs = Tabs.Count > 1;

    /// <summary>Opens a page in the current tab (rail items, palette hits, links). Pages that come from a rail item pass it for highlighting.</summary>
    public void OpenPage(object page, NavItem? nav = null)
    {
        if (!IsSessionReady) return;
        var tab = SelectedTab ?? OpenTab();
        tab.Navigate(page, nav);
        ShowTab(tab);
    }

    /// <summary>Ctrl+Enter in the palette (M3.5) and Ctrl+T: a fresh tab. Home when no page is given.</summary>
    public WorkspaceTab OpenTab(object? page = null, NavItem? nav = null)
    {
        var tab = new WorkspaceTab();
        Tabs.Add(tab);
        if (page is null)
        {
            nav = NavItems[0];
            page = CreatePage(nav);
        }

        tab.Navigate(page, nav);
        SelectedTab = tab;
        return tab;
    }

    private void ShowTab(WorkspaceTab tab)
    {
        if (!ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = tab;
            return;
        }

        CurrentPage = tab.CurrentPage;
        HighlightRail(tab.Nav);
    }

    [RelayCommand]
    private void NewTab()
    {
        if (!IsSessionReady) return;
        OpenTab();
    }

    [RelayCommand]
    private void SelectTab(WorkspaceTab tab) => SelectedTab = tab;

    /// <summary>Ctrl+W. The last tab is never closed; it resets to Home instead.</summary>
    [RelayCommand]
    private void CloseTab(WorkspaceTab? tab)
    {
        tab ??= SelectedTab;
        if (tab is null || !IsSessionReady) return;
        if (Tabs.Count == 1)
        {
            tab.DisposeAll();
            tab.Navigate(CreatePage(NavItems[0]), NavItems[0]);
            ShowTab(tab);
            return;
        }

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.DisposeAll();
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    [RelayCommand]
    private void NextTab() => CycleTab(+1);

    [RelayCommand]
    private void PreviousTab() => CycleTab(-1);

    private void CycleTab(int direction)
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        var i = (Tabs.IndexOf(SelectedTab) + direction + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[i];
    }

    /// <summary>Alt+←.</summary>
    [RelayCommand]
    private void GoBack()
    {
        if (SelectedTab is { } tab && tab.GoBack())
        {
            CurrentPage = tab.CurrentPage;
            HighlightRail(tab.Nav);
        }
    }

    // ---- the rest of the UX §11 table ---------------------------------------------------------------------------

    // ---- command palette (UX §3) ----------------------------------------------------------------------------------

    /// <summary>Null until the session is ready (needs the per-tenant SearchService).</summary>
    [ObservableProperty]
    private PaletteViewModel? _palette;

    /// <summary>Ctrl+K. Re-opening while open just re-focuses the search box.</summary>
    [RelayCommand]
    private void OpenPalette()
    {
        if (!IsSessionReady || Palette is null) return;
        if (Palette.IsOpen) Palette.IsOpen = false;
        Palette.Open();
    }

    private void OnPaletteChosen(object? sender, PaletteChoice choice)
    {
        var hit = choice.Hit;
        if (hit.ObjectType == ObjectType.Action)
        {
            RunAction(hit.Id);
            return;
        }

        _services.GetRequiredService<CacheDb>().TouchRecent(hit.Id, hit.ObjectType.ToString().ToLowerInvariant());
        object page;
        if (hit.ObjectType == ObjectType.User)
        {
            var user = _services.GetRequiredService<UserDetailViewModel>();
            user.Start(hit.Id);
            page = user;
        }
        else
        {
            page = new ObjectPageViewModel(hit);
        }

        if (choice.NewTab) OpenTab(page);
        else OpenPage(page);
        _logger.LogInformation("Opened {Type} {Id} from the palette{NewTab}", hit.ObjectType, hit.Id, choice.NewTab ? " in a new tab" : string.Empty);
    }

    private void RunAction(string id)
    {
        switch (id)
        {
            case PaletteActions.SyncNow:
                _ = SyncNowAsync();
                break;
            case PaletteActions.NewUser:
                NewUser();
                break;
            case PaletteActions.Offboard:
                ShowStatusMessage("Offboarding arrives in M6.");
                break;
            case PaletteActions.Settings:
                OpenSettings();
                break;
            default:
                _logger.LogWarning("Unknown palette action {Id}", id);
                break;
        }
    }

    /// <summary>Ctrl+N (M7).</summary>
    [RelayCommand]
    private void NewUser() => ShowStatusMessage("New user from template arrives in M7.");

    /// <summary>F5: re-read the current object from Graph. Pages opt in via <see cref="IRefreshable"/> (M4).</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (CurrentPage is IRefreshable refreshable)
        {
            await refreshable.RefreshAsync(CancellationToken.None);
            return;
        }

        ShowStatusMessage("Nothing to refresh on this page.");
    }

    /// <summary>Ctrl+Shift+S: force a delta cycle now.</summary>
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (_sync is null) return;
        ShowStatusMessage("Syncing now…");
        try
        {
            await _sync.SyncNowAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError(ex, "Sync failed");
        }
    }

    /// <summary>Transient text in the middle of the status bar; clears itself after a few seconds.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    public void ShowStatusMessage(string text, int seconds = 4)
    {
        var version = ++_statusMessageVersion;
        StatusMessage = text;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => RunOnUi(() =>
        {
            if (version == _statusMessageVersion) StatusMessage = null;
        }), TaskScheduler.Default);
    }

    // ---- first-run flow (UX §9) --------------------------------------------------------------------------------

    /// <summary>Called once after the window is shown. Silent sign-in when the cache has an account; otherwise the sign-in page.</summary>
    public async Task StartAsync()
    {
        _signInPage = new SignInPageViewModel(BeginInteractiveSignInAsync);
        CurrentPage = _signInPage;
        try
        {
            await _auth.GetTokenAsync(Scopes.Read, CancellationToken.None);
            _logger.LogInformation("Silent sign-in succeeded");
            await CompleteSignInAsync();
        }
        catch (SignInRequiredException)
        {
            _logger.LogInformation("No cached account; showing sign-in page");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup sign-in failed");
            _signInPage.ShowFailure(ex);
            CurrentPage = _signInPage;
        }
    }

    /// <summary>Sign-in button: explain the read scopes first (once per process), then prompt.</summary>
    private Task BeginInteractiveSignInAsync()
    {
        _signInPage!.ClearError();
        if (!_scopesExplained)
        {
            CurrentPage = new ScopesPageViewModel(InteractiveSignInAsync, () => CurrentPage = _signInPage!);
            return Task.CompletedTask;
        }

        return InteractiveSignInAsync();
    }

    private async Task InteractiveSignInAsync()
    {
        _scopesExplained = true;
        var scopesPage = CurrentPage as ScopesPageViewModel;
        if (scopesPage is not null) scopesPage.IsBusy = true;
        _signInPage!.IsBusy = true;
        try
        {
            await _auth.EnsureSignedInAsync(Scopes.Read, CancellationToken.None);
            await CompleteSignInAsync();
        }
        catch (SignInException ex)
        {
            _signInPage.ShowFailure(ex);
            CurrentPage = _signInPage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Interactive sign-in failed");
            _signInPage.ShowFailure(ex);
            CurrentPage = _signInPage;
        }
        finally
        {
            _signInPage.IsBusy = false;
            if (scopesPage is not null) scopesPage.IsBusy = false;
        }
    }

    /// <summary>Loads the tenant, attaches the per-tenant services, then either lands on Home or shows the progress page.</summary>
    private async Task CompleteSignInAsync()
    {
        _signInPage!.BusyText = "Loading your tenant…";
        _signInPage.IsBusy = true;
        try
        {
            await _tenantLoader.LoadAsync(CancellationToken.None);
        }
        finally
        {
            _signInPage.IsBusy = false;
        }

        AttachSession();
        if (_sync!.IsInitialSyncComplete)
        {
            EnterHome();
            return;
        }

        var progress = new SyncProgressPageViewModel(_sync);
        progress.UsersReady += (_, _) => RunOnUi(EnterHome);
        CurrentPage = progress;
    }

    private void EnterHome()
    {
        if (IsSessionReady) return;
        (CurrentPage as IDisposable)?.Dispose();
        IsSessionReady = true;
        OpenTab();
        // UX §9 step 4: land on Home with the palette open.
        OpenPalette();
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

    /// <summary>Wires the sync pill and status bar once the per-tenant services exist.</summary>
    private void AttachSession()
    {
        RefreshTenantHeader();
        if (_sync is not null) return;
        _sync = _services.GetRequiredService<ISyncStatus>();
        _sync.ProgressChanged += (_, p) => RunOnUi(() => OnSyncProgress(p));
        _sync.CycleCompleted += (_, _) => RunOnUi(OnCycleCompleted);
        Palette = _services.GetRequiredService<PaletteViewModel>();
        Palette.Chosen += OnPaletteChosen;
        SyncState = SyncPillState.Syncing;
        SyncStatusText = "Preparing…";
        var last = _services.GetRequiredService<CacheDb>().GetAllSyncState().Select(s => s.LastSync).Max();
        CacheAgeText = $"Cache {Age(last, DateTimeOffset.UtcNow)}";
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
    }

    /// <summary>Fires after every whole cycle, with IsRunning already false — the only reliable moment to settle the pill.</summary>
    private void OnCycleCompleted()
    {
        if (_sync is null) return;
        SyncState = _sync.LastError is null ? SyncPillState.Ok : SyncPillState.Error;
        SyncStatusText = _sync.LastError is null ? "Synced" : "Sync errors";
        var now = DateTimeOffset.UtcNow;
        CacheAgeText = $"Cache {Age(_sync.LastCycleCompleted, now)}";
        LastDeltaText = $"Last delta {Age(_sync.LastCycleCompleted, now)}";
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
