using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.App.ViewModels.Pages;
using AdminConsole.App.ViewModels.Palette;
using AdminConsole.Core;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Groups;
using AdminConsole.Core.Sync;
using AdminConsole.Core.Templates;
using AdminConsole.Core.Users;
using AdminConsole.Core.Users.BulkEdit;
using AdminConsole.Core.Search;
using AdminConsole.Core.Offboarding;

namespace AdminConsole.App.ViewModels;

/// <summary>
/// Root of the UI (UX §2 shell): title bar state, nav rail, tabs + content region with per-tab back stacks, status bar,
/// the non-modal error bar, the first-run flow (UX §9) and the keyboard table (UX §11). One instance per process.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _root;
    private readonly TenantSessionFactory _factory;
    private readonly TenantConfigStore _profiles;
    private readonly ILogger<ShellViewModel> _logger;

    // M20.2: every open tenant, and the one the selected tab belongs to. Everything below that used to be a single
    // app-wide service is now the active session's, so the code that uses it did not have to change.
    private readonly List<TenantSession> _sessions = [];
    private readonly TenantContext _noTenant = new();
    private TenantSession? _session;

    private IServiceProvider _services => _session?.Services ?? _root;
    private IAuthService _auth => _session!.Auth;
    private TenantContext _tenant => _session?.Tenant ?? _noTenant;
    private TenantContextLoader _tenantLoader => _session!.Loader;
    private ISyncStatus? _sync => _session?.Sync;
    private SignInPageViewModel? _signInPage;
    private bool _scopesExplained;
    private int _statusMessageVersion;

    public ShellViewModel(IServiceProvider services, TenantSessionFactory factory, TenantConfigStore profiles, ToastService toasts, DialogService dialogs, ILogger<ShellViewModel> logger)
    {
        _root = services;
        _factory = factory;
        _profiles = profiles;
        _logger = logger;
        Toasts = toasts.Items;
        Dialogs = dialogs;

        NavItems =
        [
            new NavItem("home", "Home", SymbolRegular.Home24, 1, typeof(HomePageViewModel)),
            new NavItem("users", "Users", SymbolRegular.People24, 2, typeof(UsersPageViewModel)),
            new NavItem("groups", "Groups", SymbolRegular.PeopleTeam24, 3, typeof(GroupsPageViewModel)),
            new NavItem("devices", "Devices", SymbolRegular.Laptop24, 4, typeof(DevicesPageViewModel)),
            new NavItem("templates", "Templates", SymbolRegular.DocumentBulletList24, 5, typeof(TemplatesPageViewModel)),
            new NavItem("reports", "Reports", SymbolRegular.DataBarVertical24, 6, typeof(ReportsPageViewModel)),
            new NavItem("activity", "Activity log", SymbolRegular.History24, 7, typeof(ActivityPageViewModel)),
            // M12.3: on-prem AD. It sits below the cloud pages because most tenants have no domain, and the page
            // says so plainly rather than looking broken.
            new NavItem("domain", "Domain", SymbolRegular.Server24, 8, typeof(DomainPageViewModel)),
        ];
        SettingsItem = new NavItem("settings", "Settings", SymbolRegular.Settings24, 0, typeof(SettingsPageViewModel));

        Tabs.CollectionChanged += OnTabsChanged;
    }

    /// <summary>What this installation calls itself (M11.4). The title bar and sign-in page bind to it, so a client
    /// console carries the client's name rather than ours.</summary>
    public string ProductName => _services.GetRequiredService<BrandingStore>().Load().ProductName;

    public string Tagline => _services.GetRequiredService<BrandingStore>().Load().Tagline;

    /// <summary>UX §10 toasts, owned by <see cref="ToastService"/>; the window binds the bottom-right stack to this.</summary>
    public ObservableCollection<ToastItem> Toasts { get; }

    /// <summary>The one modal dialog the window can show (confirms, the password reset flow). Null content means none.</summary>
    public DialogService Dialogs { get; }

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
    private void Navigate(NavItem item) => NavigateWithFilters(item, null);

    private void NavigateWithFilters(NavItem item, IReadOnlyList<string>? filters)
    {
        if (!IsSessionReady) return;
        var tab = SelectedTab ?? OpenTab();
        tab.Navigate(CreatePage(item, filters), item);
        ShowTab(tab);
        _logger.LogDebug("Navigated to {Nav}", item.Id);
    }

    /// <summary>Opens a list page from a Home tile with the filters behind that figure already ticked.</summary>
    private void OpenListWithFilters(string navId, IReadOnlyList<string> filters)
    {
        if (NavItems.FirstOrDefault(i => i.Id == navId) is { } item) NavigateWithFilters(item, filters);
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

    private object CreatePage(NavItem item, IReadOnlyList<string>? filters = null)
    {
        var page = _services.GetRequiredService(item.PageViewModelType);
        if (page is TemplatesPageViewModel templates) templates.PickPerson = OpenPersonPicker;
        if (page is SettingsPageViewModel settings) settings.SignedOut = ReturnToSignIn;
        if (page is ActivityPageViewModel activity) activity.OpenObject = OpenObjectFromPage;
        if (page is ReportsPageViewModel reports)
        {
            reports.ReportStatus = text => RunOnUi(() => ShowStatusMessage(text, 8));
            _ = reports.StartAsync();
        }
        if (page is DomainPageViewModel domain)
        {
            domain.OpenObject = OpenDirectoryObject;
            domain.Start();
        }

        // The list pages (M9.2-9.4) open the object a row points at, and start their first read only once the shell
        // has finished building them.
        if (page is UsersPageViewModel users)
        {
            users.OpenRow = r => OpenObjectFromPage(ObjectType.User, r.Id, r.Primary);
            users.Files = _services.GetRequiredService<FilePicker>();
            users.ReportStatus = text => RunOnUi(() => ShowStatusMessage(text, 8));
            users.BulkEdit = ids => BulkEditAsync(users, ids);
            if (filters is not null) users.ApplyFilters(filters);
            users.Start();
        }

        if (page is GroupsPageViewModel groups)
        {
            groups.OpenRow = r => OpenObjectFromPage(ObjectType.Group, r.Id, r.Primary);
            groups.Files = _services.GetRequiredService<FilePicker>();
            groups.ReportStatus = text => RunOnUi(() => ShowStatusMessage(text, 8));
            if (filters is not null) groups.ApplyFilters(filters);
            groups.Start();
        }

        if (page is DevicesPageViewModel devices)
        {
            devices.OpenRow = r => OpenObjectFromPage(ObjectType.Device, r.Id, r.Primary);
            devices.Files = _services.GetRequiredService<FilePicker>();
            devices.ReportStatus = text => RunOnUi(() => ShowStatusMessage(text, 8));
            if (filters is not null) devices.ApplyFilters(filters);
            devices.Start();
        }

        // Home (M9.5) is a launchpad: nearly everything on it runs one of the shell's own actions.
        if (page is HomePageViewModel home)
        {
            home.OpenPalette = OpenPalette;
            home.NewUser = () => NewUserCommand.Execute(null);
            home.Offboard = Offboard;
            home.OffboardMany = () => OffboardListCommand.Execute(null);
            home.SyncNow = () => SyncNowCommand.Execute(null);
            home.OpenObject = OpenObjectFromPage;
            home.OpenList = OpenListWithFilters;
            _ = home.LoadAsync(CancellationToken.None);
        }

        if (page is PlaceholderPageViewModel placeholder)
        {
            placeholder.Title = item.Title;
            placeholder.ArrivesIn = string.Empty;
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
        if (value.Session is { } session && !ReferenceEquals(session, _session)) ShowSession(session);
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
        var tab = new WorkspaceTab { Session = _session };
        Tabs.Add(tab);
        RefreshTabLabels();
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
        // Any navigation dismisses the palette; leaving it open would cover the page the admin just asked for.
        if (Palette is { IsOpen: true }) Palette.IsOpen = false;

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
        RefreshTabLabels();
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
        // Ctrl+K is always a plain search, so drop any picker the admin abandoned with Esc.
        _pendingGroupPick = null;
        _pendingPersonPick = null;
        if (Palette.IsOpen) Palette.IsOpen = false;
        Palette.Open();
    }

    private Func<DirectoryGroup, Task>? _pendingGroupPick;
    private Action<PersonRef>? _pendingPersonPick;

    /// <summary>Opens the palette restricted to groups and hands the pick to <paramref name="onPicked"/> (UX §4: group picker).</summary>
    private void OpenGroupPicker(Func<DirectoryGroup, Task> onPicked)
    {
        if (Palette is null) return;
        _pendingGroupPick = onPicked;
        Palette.OpenPicker(ObjectType.Group, "Choose a group to add this user to");
    }

    /// <summary>
    /// Opens the palette restricted to users and hands the pick back. The prompt comes from the caller: this picker
    /// serves the offboarding delegate, a new user's manager and the template snapshot, and it used to ask all three
    /// "Choose who inherits ownership and access", which is only true of the first.
    /// </summary>
    private void OpenPersonPicker(string prompt, Action<PersonRef> onPicked)
    {
        if (Palette is null) return;
        _pendingPersonPick = onPicked;
        Palette.OpenPicker(ObjectType.User, prompt);
    }

    private void OnPaletteChosen(object? sender, PaletteChoice choice)
    {
        var hit = choice.Hit;

        if (_pendingPersonPick is { } personPick)
        {
            _pendingPersonPick = null;
            if (hit.ObjectType != ObjectType.User) return;
            var picked = _services.GetRequiredService<CacheDb>().GetUser(hit.Id);
            if (picked is not null) personPick(new PersonRef(picked.Id, picked.DisplayName, picked.UserPrincipalName));
            return;
        }

        if (_pendingGroupPick is { } pick)
        {
            _pendingGroupPick = null;
            if (hit.ObjectType != ObjectType.Group) return;
            var group = _services.GetRequiredService<CacheDb>().GetGroup(hit.Id);
            if (group is not null) _ = pick(group);
            return;
        }

        if (hit.ObjectType == ObjectType.Action)
        {
            RunAction(hit.Id);
            return;
        }

        _services.GetRequiredService<CacheDb>().TouchRecent(hit.Id, hit.ObjectType.ToString().ToLowerInvariant());
        var page = CreateObjectPage(hit.ObjectType, hit.Id, hit);
        if (choice.NewTab) OpenTab(page);
        else OpenPage(page);
        _logger.LogInformation("Opened {Type} {Id} from the palette{NewTab}", hit.ObjectType, hit.Id, choice.NewTab ? " in a new tab" : string.Empty);
    }

    /// <summary>Object pages by type. Links inside pages (device cards, manager, members) come through here too.</summary>
    private object CreateObjectPage(ObjectType type, string id, SearchHit? hit = null)
    {
        switch (type)
        {
            case ObjectType.User:
                var user = _services.GetRequiredService<UserDetailViewModel>();
                user.OpenObject = OpenObjectFromPage;
                user.PickGroup = OpenGroupPicker;
                user.PickPerson = OpenPersonPicker;
                user.ReportStatus = text => RunOnUi(() => ShowStatusMessage(text, 8));
                user.Start(id);
                return user;
            case ObjectType.Device:
                var device = _services.GetRequiredService<DeviceDetailViewModel>();
                device.OpenObject = OpenObjectFromPage;
                device.Start(id);
                return device;
            case ObjectType.Group:
                var group = _services.GetRequiredService<GroupDetailViewModel>();
                group.OpenObject = OpenObjectFromPage;
                group.Start(id);
                return group;
            default:
                hit ??= new SearchHit { ObjectType = type, Id = id, Primary = id, Score = 0 };
                return new ObjectPageViewModel(hit);
        }
    }

    private void OpenObjectFromPage(ObjectType type, string id, string title)
    {
        _services.GetRequiredService<CacheDb>().TouchRecent(id, type.ToString().ToLowerInvariant());
        OpenPage(CreateObjectPage(type, id, new SearchHit { ObjectType = type, Id = id, Primary = title, Score = 0 }));
    }

    /// <summary>
    /// Opens what a Domain row points at (M12.9): the object's real page where the cloud knows about it, and the
    /// AD-only page where it does not.
    /// </summary>
    /// <remarks>
    /// A synced user has a User 360 with licences, groups, sign-in activity and devices on it; sending an admin to a
    /// bare attribute sheet instead, purely because they arrived from the Domain list rather than the Users list,
    /// makes the same person look like two different objects depending on the door used. So the row resolves to the
    /// cloud object first and only falls back when there genuinely is nothing else to show.
    ///
    /// The joins are the ones M12.4 proved: <c>onPremisesImmutableId</c> for a user, and for a computer the Entra
    /// <c>deviceId</c>, which for a hybrid join is the AD <c>objectGUID</c> itself.
    /// </remarks>
    private void OpenDirectoryObject(Guid objectGuid, OnPremKind kind)
    {
        _ = OpenDirectoryObjectAsync(objectGuid, kind);
    }

    private async Task OpenDirectoryObjectAsync(Guid objectGuid, OnPremKind kind)
    {
        try
        {
            var directory = _services.GetRequiredService<IDirectoryService>();
            var cache = _services.GetRequiredService<CacheDb>();

            if (kind == OnPremKind.User &&
                await directory.GetUserAsync(objectGuid, CancellationToken.None) is { } adUser)
            {
                var anchor = Convert.ToBase64String(adUser.ObjectGuid.ToByteArray());
                if (cache.FindUserByImmutableId(anchor) is { } cloudUser)
                {
                    OpenObjectFromPage(ObjectType.User, cloudUser.Id, cloudUser.DisplayName ?? cloudUser.UserPrincipalName);
                    return;
                }
            }
            else if (kind == OnPremKind.Computer &&
                     await directory.GetComputerAsync(objectGuid, CancellationToken.None) is { } adComputer)
            {
                // A hybrid-joined machine's Entra deviceId is its AD objectGUID, so this is a direct lookup rather
                // than a name match - which would pair the wrong machine the first time two of them shared a name.
                if (cache.GetEntraDeviceByDeviceId(adComputer.ObjectGuid.ToString()) is { } device)
                {
                    OpenObjectFromPage(ObjectType.Device, device.Id, device.DisplayName ?? adComputer.Name);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Not being able to reach the directory is not a reason to open nothing: the AD page states that case
            // for itself.
            _services.GetRequiredService<ILogger<ShellViewModel>>()
                .LogWarning(ex, "Could not resolve the cloud object behind {Guid}", objectGuid);
        }

        OpenAdObjectPage(objectGuid, kind);
    }

    /// <summary>The AD-only view, for an object the cloud has never heard of.</summary>
    private void OpenAdObjectPage(Guid objectGuid, OnPremKind kind)
    {
        var page = _services.GetRequiredService<DomainObjectViewModel>();

        // Back goes to the Domain list rather than closing the tab: an admin who has just deleted something is
        // usually about to look at the next thing, not to leave.
        page.GoBack = () =>
        {
            if (NavItems.FirstOrDefault(i => i.Id == "domain") is { } item) Navigate(item);
        };

        page.Start(objectGuid, kind);
        OpenPage(page);
    }

    private void RunAction(string id)
    {
        switch (id)
        {
            case PaletteActions.SyncNow:
                _ = SyncNowAsync();
                break;
            case PaletteActions.NewUser:
                _ = NewUserAsync();
                break;
            case PaletteActions.Offboard:
                Offboard();
                break;
            case PaletteActions.Settings:
                OpenSettings();
                break;
            default:
                _logger.LogWarning("Unknown palette action {Id}", id);
                break;
        }
    }

    /// <summary>
    /// Ctrl+Shift+O and the palette's "Offboard user…". On a user page it offboards that user; anywhere else it opens
    /// the palette to pick one first, because offboarding without naming the target is not an action anyone wants.
    /// </summary>
    [RelayCommand]
    private void Offboard()
    {
        if (CurrentPage is UserDetailViewModel user)
        {
            user.OffboardCommand.Execute(null);
            return;
        }

        if (Palette is null) return;
        _pendingPersonPick = person => OpenObjectFromPage(ObjectType.User, person.Id, person.Label);
        Palette.OpenPicker(ObjectType.User, "Choose the user to offboard");
        ShowStatusMessage("Choose a user, then press Ctrl+Shift+O on their page.");
    }

    /// <summary>M18.3: the selected people, edited together. The list re-reads afterwards so it shows what was written.</summary>
    private async Task BulkEditAsync(UsersPageViewModel users, IReadOnlyList<string> ids)
    {
        if (!IsSessionReady || ids.Count == 0) return;
        var dialog = new BulkEditDialogViewModel(
            _services.GetRequiredService<BulkEditService>(),
            ids,
            _services.GetRequiredService<PermissionChecker>(),
            _services.GetRequiredService<ConsentService>());
        await Dialogs.ShowAsync(dialog);
        if (dialog.HasRun)
        {
            users.ClearSelectionCommand.Execute(null);
            await users.RefreshAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Offboard a list (M15.5). The list dialog settles who is who; then each person gets the ordinary offboard dialog
    /// with its own preview, one after another. Closing one without running it stops the list there, and the status bar
    /// names who was not reached — a list that quietly skipped someone would be worse than no list.
    /// </summary>
    [RelayCommand]
    private async Task OffboardListAsync()
    {
        if (!IsSessionReady) return;

        var list = new OffboardListDialogViewModel(_services.GetRequiredService<CacheDb>(), _services.GetRequiredService<SearchService>());
        if (!await Dialogs.ShowAsync(list)) return;

        var people = list.Chosen;
        var users = _services.GetRequiredService<UserService>();
        var done = 0;
        for (var i = 0; i < people.Count; i++)
        {
            DirectoryUser target;
            try
            {
                target = await users.ReadFreshAsync(people[i].Id, CancellationToken.None) ?? people[i];
            }
            catch (GraphException ex)
            {
                ShowStatusMessage($"Stopped: {people[i].DisplayName} could not be read ({GraphErrorText.Friendly(ex)}). Not reached: {Names(people.Skip(i))}.");
                return;
            }

            var dialog = new OffboardDialogViewModel(
                target,
                _services.GetRequiredService<OffboardingRunner>(),
                _services.GetRequiredService<OffboardingProfileStore>(),
                _services.GetRequiredService<PermissionChecker>(),
                _services.GetRequiredService<ConsentService>(),
                _services.GetRequiredService<ActionLog>(),
                _tenant,
                _logger)
            {
                PickDelegate = OpenPersonPicker,
                ListPosition = $"{i + 1} of {people.Count}",
            };

            await dialog.RefreshPreviewAsync();
            await Dialogs.ShowAsync(dialog);
            if (!dialog.HasRun)
            {
                ShowStatusMessage(done == 0
                    ? $"Stopped before anyone was offboarded. Not reached: {Names(people.Skip(i))}."
                    : $"Stopped after {done} of {people.Count}. Not offboarded: {Names(people.Skip(i))}.");
                return;
            }

            done++;
        }

        ShowStatusMessage($"Offboarded {done} of {people.Count} people. Each run is on the Activity page.");
    }

    private static string Names(IEnumerable<DirectoryUser> people) => string.Join(", ", people.Select(p => p.DisplayName ?? p.UserPrincipalName));

    /// <summary>Ctrl+N and the palette's "New user from template" (doc 07 §flow).</summary>
    [RelayCommand]
    private async Task NewUserAsync()
    {
        if (!IsSessionReady) return;

        // M17.2: whether this create can go to AD, asked before the dialog opens so the form shows the right fields.
        var directory = _services.GetRequiredService<IDirectoryService>();
        DirectoryStatus? adStatus = null;
        try
        {
            adStatus = await directory.GetStatusAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("AD status unavailable for create: {Message}", ex.Message);
        }

        var dialog = new CreateUserDialogViewModel(
            _services.GetRequiredService<TemplateService>(),
            _services.GetRequiredService<UserProvisioningService>(),
            _services.GetRequiredService<CacheDb>(),
            _tenant,
            _services.GetRequiredService<PermissionChecker>(),
            _services.GetRequiredService<ConsentService>(),
            _logger)
        {
            PickPerson = OpenPersonPicker,
            PickGroup = onPicked => OpenGroupPicker(group =>
            {
                onPicked(group);
                return Task.CompletedTask;
            }),
            OpenUser = (id, title) => OpenObjectFromPage(ObjectType.User, id, title),
            Hybrid = _services.GetRequiredService<HybridProvisioningService>(),
            AdDomain = adStatus is { IsConnected: true } ? adStatus.DomainName : null,
            AdReason = adStatus?.Reason,
        };

        await _services.GetRequiredService<DialogService>().ShowAsync(dialog);
    }

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
        // M10.2: an installation that does not know which registration to use cannot sign in, and failing inside MSAL
        // tells the person in front of it nothing they can act on. Ask first.
        var profiles = _profiles.LoadProfiles();
        if (profiles.Default is not { IsConfigured: true } profile)
        {
            _logger.LogInformation("No app registration configured; showing setup");
            CurrentPage = new SetupPageViewModel(_profiles, RestartAfterSetup);
            return;
        }

        _session = OpenSession(profile, profiles);

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

    /// <summary>
    /// The registration only reaches MSAL when the auth service is built, so a newly configured install has to start
    /// again rather than carry on with a client that was created without one. Saying so is better than a restart that
    /// looks like a crash.
    /// </summary>
    private void RestartAfterSetup()
    {
        ShowStatusMessage("Registration saved. Restart the console to sign in.");
        CurrentPage = new SetupSavedViewModel();
    }

    /// <summary>
    /// Back to the sign-in page after a sign-out (UX §12 Account). The tabs go with it: every page behind them was
    /// built for an account that is no longer signed in.
    /// </summary>
    private void ReturnToSignIn()
    {
        var closing = _session;
        foreach (var tab in Tabs.Where(t => ReferenceEquals(t.Session, closing)).ToList())
        {
            Tabs.Remove(tab);
            tab.DisposeAll();
        }

        if (closing is not null)
        {
            _sessions.Remove(closing);
            _ = closing.DisposeAsync().AsTask();
        }

        // M20: another tenant still open carries on; the window only returns to sign-in when none is left.
        if (Tabs.FirstOrDefault() is { } remaining)
        {
            SelectedTab = remaining;
            RefreshTabLabels();
            RefreshTenantChoices();
            ShowStatusMessage($"Signed out of {closing?.Label ?? "that tenant"}.");
            return;
        }

        IsSessionReady = false;
        Tabs.Clear();
        Palette = null;
        _session = closing is null ? null : OpenSession(closing.Profile, _profiles.LoadProfiles());
        _signInPage ??= new SignInPageViewModel(BeginInteractiveSignInAsync);
        _scopesExplained = false;
        CurrentPage = _signInPage;
        HighlightRail(null);
        RefreshTenantHeader();
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

        Remember(_session!);
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
        // UX §9 step 4 said to land with the palette open. That was written when Home was a single sentence telling
        // you to press Ctrl+K, so there was nothing for the overlay to cover. Home is now a page worth reading, and
        // opening the palette on top of it hides half of it behind a second search box — it reads as a bug. Home
        // carries its own search, captioned with the shortcut, so the palette is still one keystroke and is now
        // discoverable rather than imposed. Recorded in DECISIONS.
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
        var session = _session!;
        if (!session.IsStarted)
        {
            session.Start();
            // Events from a tenant that is not the one on screen change nothing on screen; the pill and the status bar
            // are re-read from the session when its tab is selected.
            session.Tenant.Changed += (_, _) => RunOnUi(() => { if (ReferenceEquals(session, _session)) RefreshTenantHeader(); });
            session.Sync!.ProgressChanged += (_, p) => RunOnUi(() => { if (ReferenceEquals(session, _session)) OnSyncProgress(p); });
            session.Sync.CycleCompleted += (_, _) => RunOnUi(() => { if (ReferenceEquals(session, _session)) OnCycleCompleted(); });
            session.Palette.Chosen += OnPaletteChosen;
        }

        ShowSession(session);
    }

    /// <summary>Makes a session the one on screen: title bar, sync pill, status bar and palette are its own.</summary>
    private void ShowSession(TenantSession session)
    {
        _session = session;
        Palette = session.Palette;
        RefreshTenantHeader();
        if (session.Sync is { } sync)
        {
            if (sync.IsRunning || sync.LastCycleCompleted is null)
            {
                SyncState = SyncPillState.Syncing;
                SyncStatusText = sync.IsInitialSyncComplete ? "Syncing…" : "Preparing…";
            }
            else
            {
                OnCycleCompleted();
            }
        }

        if (session.Tenant.IsLoaded)
        {
            var last = session.Services.GetRequiredService<CacheDb>().GetAllSyncState().Select(s => s.LastSync).Max();
            CacheAgeText = $"Cache {Age(last, DateTimeOffset.UtcNow)}";
        }

        RefreshTenantChoices();
    }

    // ---- tenants (M20) ----------------------------------------------------------------------------------------------

    private TenantSession OpenSession(TenantProfile profile, TenantProfileList list)
    {
        var session = _factory.Create(profile, isPrimary: list.Profiles.FirstOrDefault()?.Id == profile.Id);
        _sessions.Add(session);
        return session;
    }

    /// <summary>Saves what the sign-in established: the tenant's id and name, and the account, so it reopens as itself.</summary>
    private void Remember(TenantSession session)
    {
        var t = session.Tenant;
        var profile = session.Profile with
        {
            TenantId = t.TenantId,
            Name = t.TenantDisplayName.Length > 0 ? t.TenantDisplayName : session.Profile.Name,
            AccountId = session.Auth.CurrentAccount?.HomeAccountId is { Length: > 0 } a ? a : session.Profile.AccountId,
            Username = t.SignedInUpn,
        };
        session.Profile = profile;
        var list = _profiles.LoadProfiles();
        _profiles.SaveProfiles(list.With(profile) with { LastUsedId = profile.Id });
    }

    /// <summary>The title-bar switcher's entries.</summary>
    public ObservableCollection<TenantChoice> TenantChoices { get; } = [];

    [ObservableProperty]
    private bool _isTenantMenuOpen;

    // Built as the menu opens, so its tab counts are the ones on screen now.
    partial void OnIsTenantMenuOpenChanged(bool value)
    {
        if (value) RefreshTenantChoices();
    }

    private void RefreshTenantChoices()
    {
        TenantChoices.Clear();
        foreach (var p in _profiles.LoadProfiles().Profiles)
        {
            var open = _sessions.FirstOrDefault(s => s.Profile.Id == p.Id && s.Tenant.IsLoaded);
            var tabs = open is null ? 0 : Tabs.Count(t => ReferenceEquals(t.Session, open));
            var detail = open is null ? "Not open" : tabs == 1 ? "Open · 1 tab" : $"Open · {tabs} tabs";
            TenantChoices.Add(new TenantChoice(p.Id, open?.Label ?? p.DisplayName, detail, ReferenceEquals(open, _session), OpenTenantAsync));
        }
    }

    /// <summary>Badges every tab with its tenant while more than one tenant has tabs open; none when only one does.</summary>
    private void RefreshTabLabels()
    {
        var several = Tabs.Select(t => t.Session).Distinct().Count() > 1;
        foreach (var t in Tabs) t.TenantLabel = several ? t.Session?.Label : null;
    }

    /// <summary>
    /// The switcher. An open tenant gets its most recent tab back (or a new one). A tenant not open yet is signed in to —
    /// silently when its account is cached — loaded, and opened in a new tab beside the others.
    /// </summary>
    private async Task OpenTenantAsync(TenantChoice choice)
    {
        IsTenantMenuOpen = false;
        if (_sessions.FirstOrDefault(s => s.Profile.Id == choice.ProfileId && s.Tenant.IsLoaded) is { } open)
        {
            if (Tabs.LastOrDefault(t => ReferenceEquals(t.Session, open)) is { } tab) SelectedTab = tab;
            else
            {
                ShowSession(open);
                OpenTab();
            }

            return;
        }

        var list = _profiles.LoadProfiles();
        if (list.Profiles.FirstOrDefault(p => p.Id == choice.ProfileId) is not { } profile) return;
        await OpenNewTenantAsync(profile, list);
    }

    private async Task OpenNewTenantAsync(TenantProfile profile, TenantProfileList list)
    {
        var session = OpenSession(profile, list);
        ShowStatusMessage($"Signing in to {profile.DisplayName}…", 60);
        try
        {
            try
            {
                await session.Auth.GetTokenAsync(Scopes.Read, CancellationToken.None);
            }
            catch (SignInRequiredException)
            {
                await session.Auth.EnsureSignedInAsync(Scopes.Read, CancellationToken.None);
            }

            await session.Loader.LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _sessions.Remove(session);
            await session.DisposeAsync();
            _logger.LogWarning(ex, "Opening profile {Profile} failed", profile.Id);
            ShowError(ex, $"Could not open {profile.DisplayName}");
            return;
        }

        if (_sessions.FirstOrDefault(s => !ReferenceEquals(s, session) && s.Tenant.IsLoaded && s.Tenant.TenantId == session.Tenant.TenantId) is { } same)
        {
            _sessions.Remove(session);
            await session.DisposeAsync();
            ShowStatusMessage($"{same.Label} is already open.");
            if (Tabs.LastOrDefault(t => ReferenceEquals(t.Session, same)) is { } tab) SelectedTab = tab;
            return;
        }

        Remember(session);
        _session = session;
        AttachSession();
        OpenTab();
        ShowStatusMessage($"Opened {session.Label}. Its first sync runs in the background.", 8);
    }

    /// <summary>
    /// Adds a tenant to the switcher: which tenant (an id or a verified domain), and the client id of the registration
    /// to sign in to it with. Each tenant normally has its own (DECISIONS, M10), made there by the script; left empty,
    /// the current tenant's registration is used, which works where it has been consented.
    /// </summary>
    [RelayCommand]
    private async Task AddTenantAsync()
    {
        IsTenantMenuOpen = false;
        var prompt = new TextPromptDialogViewModel("Add a tenant",
            "The tenant's ID or one of its domains. This installation's app registration must already be consented there (scripts/New-AppRegistration.ps1, or the admin consent link in doc 08).",
            "contoso.onmicrosoft.com");
        if (!await Dialogs.ShowAsync(prompt)) return;

        var tenant = prompt.Value.Trim();
        var current = _session?.Profile ?? _profiles.LoadProfiles().Default;
        var clientPrompt = new TextPromptDialogViewModel("Add a tenant",
            $"The client ID of {tenant}'s own app registration, made there by scripts/New-AppRegistration.ps1. Leave it empty to use this tenant's registration, if it has been consented there.",
            current?.ClientId)
        {
            AllowEmpty = true,
            Validate = v => TenantConfig.LooksLikeClientId(v) ? null : "A client ID is a GUID, like 00000000-0000-0000-0000-000000000000.",
        };
        if (!await Dialogs.ShowAsync(clientPrompt)) return;

        var list = _profiles.LoadProfiles();
        if (list.Profiles.FirstOrDefault(p => string.Equals(p.TenantId, tenant, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            await OpenTenantAsync(new TenantChoice(known.Id, known.DisplayName, string.Empty, false, OpenTenantAsync));
            return;
        }

        if (current is null) return;
        var clientId = clientPrompt.Value.Trim().Length > 0 ? clientPrompt.Value.Trim() : current.ClientId;
        var profile = new TenantProfile { Name = tenant, TenantId = tenant, ClientId = clientId, Authority = current.Authority };
        list = list.With(profile);
        _profiles.SaveProfiles(list);
        await OpenNewTenantAsync(profile, list);
        RefreshTenantChoices();
    }

    /// <summary>On exit: each tenant's sync loop stops and its cache closes.</summary>
    public async Task CloseSessionsAsync()
    {
        foreach (var s in _sessions.ToList()) await s.DisposeAsync();
        _sessions.Clear();
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
        CacheEntity.Skus => "licences",
        CacheEntity.Organization => "tenant details",
        CacheEntity.Mailboxes => "mailboxes",
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
