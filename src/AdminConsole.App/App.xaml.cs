using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using AdminConsole.App.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels;
using AdminConsole.App.ViewModels.Pages;
using AdminConsole.App.ViewModels.Palette;
using AdminConsole.App.Views;
using AdminConsole.Core;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Devices;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Licensing;
using AdminConsole.Core.Groups;
using AdminConsole.Core.Search;
using AdminConsole.Core.Sync;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Restore;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.OneDrive;
using AdminConsole.Core.SharePoint;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.Templates;
using AdminConsole.Core.Users;

namespace AdminConsole.App;

/// <summary>Composition root (doc 03 §Dependency injection). Builds the host, wires DI and logging, owns the global exception handlers.</summary>
public sealed partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started");

    private bool _migratedLegacyData;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything opens a file under it: an install made before the M11 rename keeps its cache, templates,
        // action log and token cache. No logger exists this early, so the outcome is reported once the host is up.
        _migratedLegacyData = AppPaths.MigrateLegacyRoot();
        AppPaths.EnsureCreated();

        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureAppConfiguration(config =>
            {
                config.Sources.Clear();
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
                config.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddFilter("AdminConsole", LogLevel.Debug);
                logging.AddProvider(new FileLoggerProvider(AppPaths.LogsDirectory));
            })
            .ConfigureServices(ConfigureServices)
            .Build();

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        HookGlobalExceptionHandlers();

        await _host.StartAsync();
        _logger.LogInformation("Admin Console {Version} starting", typeof(App).Assembly.GetName().Version);
        if (_migratedLegacyData) _logger.LogInformation("Carried the previous installation's data forward from {Old}", AppPaths.LegacyRoot);

        // CLAUDE.md: apply the theme before the window is shown. The accent is whatever this installation is branded with.
        // The saved appearance, before any window exists: applying it later would flash the wrong theme.
        var settings = _host.Services.GetRequiredService<UserSettingsStore>().Load();
        var branding = _host.Services.GetRequiredService<BrandingStore>().Load();
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.None, false);
        AppearanceService.ApplyAll(settings, branding);

        // The sync loop reads its intervals each cycle, so the saved choice takes effect from the first one.
        var syncOptions = _host.Services.GetRequiredService<SyncOptions>();
        syncOptions.DeltaIntervalSeconds = settings.DeltaIntervalSeconds;
        syncOptions.DeviceRefreshIntervalSeconds = settings.DeviceRefreshIntervalSeconds;

        var window = _host.Services.GetRequiredService<MainWindow>();
        var handles = _host.Services.GetRequiredService<WindowHandleProvider>();
        window.SourceInitialized += (_, _) => handles.Handle = new WindowInteropHelper(window).Handle;
        MainWindow = window;
        window.Show();

        // First-run flow (UX §9): silent sign-in if possible, otherwise the sign-in page. Runs after the HWND exists (WAM needs it).
        await _host.Services.GetRequiredService<ShellViewModel>().StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _logger?.LogInformation("Admin Console exiting ({Code})", e.ApplicationExitCode);
            try
            {
                await _host.Services.GetRequiredService<ShellViewModel>().CloseSessionsAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Closing tenant sessions on exit failed");
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _host.StopAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // best effort on shutdown
            }

            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(HostBuilderContext ctx, IServiceCollection services)
    {
        // The registration the build shipped with: the fallback when tenant.json has no profile yet (M10.1).
        services.AddSingleton(ctx.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions());
        services.AddSingleton(ctx.Configuration.GetSection(SyncOptions.SectionName).Get<SyncOptions>() ?? new SyncOptions());

        // M12: on-prem AD from appsettings. Since M20 it belongs to the first tenant profile; other profiles carry
        // their own domains (TenantProfile.Domains), which bind as the signed-in Windows user.
        services.AddSingleton(ctx.Configuration.GetSection(LdapOptions.SectionName).Get<LdapOptions>() ?? new LdapOptions());

        // M10.1: which registration this installation uses comes from %LOCALAPPDATA%, with appsettings.json as the
        // fallback. A client installation can be pointed at their own registration without editing a file under
        // Program Files that the next update would overwrite anyway.
        services.AddSingleton(sp => new BrandingStore(
            BrandingStore.DefaultPath,
            sp.GetRequiredService<ILogger<BrandingStore>>()));

        services.AddSingleton(sp => new TenantConfigStore(
            TenantConfigStore.DefaultPath,
            sp.GetRequiredService<AuthOptions>(),
            sp.GetRequiredService<ILogger<TenantConfigStore>>()));

        services.AddSingleton<WindowHandleProvider>();
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));
        services.AddSingleton(sp => ActionLog.OpenFile(AppPaths.ActionsDbPath, sp.GetRequiredService<ILogger<ActionLog>>()));
        services.AddSingleton(_ => new SnapshotStore(SnapshotStore.DefaultRoot));
        services.AddSingleton(sp => new UserSettingsStore(UserSettingsStore.DefaultPath, sp.GetRequiredService<ILogger<UserSettingsStore>>()));
        services.AddSingleton(_ => new PendingProvisioningStore(PendingProvisioningStore.DefaultDirectory));
        services.AddSingleton(new AdminConsole.Core.Reports.ReportStore(AdminConsole.Core.Reports.ReportStore.DefaultRoot));
        services.AddSingleton(sp => new OffboardingProfileStore(
            OffboardingProfileStore.DefaultDirectory,
            sp.GetRequiredService<ILogger<OffboardingProfileStore>>()));

        services.AddSingleton<ToastService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<FilePicker>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();

        // M20.2: everything that belongs to one tenant lives in a session container of its own (ConfigureTenantServices).
        services.AddSingleton(sp => new TenantSessionFactory(sp, sp.GetRequiredService<LdapOptions>(), sp.GetRequiredService<ILogger<TenantSessionFactory>>()));
    }

    /// <summary>
    /// One tenant's services (M20.2), built into a <see cref="TenantSession"/>'s own container. Before M20 these were
    /// app-wide singletons; they are still singletons, but per tenant, so two tabs on two tenants never share a cache,
    /// a token or a sync loop.
    /// </summary>
    internal static void ConfigureTenantServices(IServiceCollection services, TenantProfile profile, IReadOnlyList<LdapOptions> domains, bool isPrimary)
    {
        services.AddSingleton(new AuthOptions
        {
            ClientId = profile.ClientId,
            Authority = profile.EffectiveAuthority,
            AccountId = profile.AccountId,
            TenantId = profile.TenantId,
            // A profile that has never been signed in, when it is not the only one, asks which account to use.
            PickAccount = profile.AccountId is null && profile.TenantId is not null,
        });

        // M12 / M20.3: on-prem. No domain is the cloud-only case (every on-prem feature greyed with that reason); one is
        // one connection; several go through the router. LdapOptions is the first domain's, for the settings that are
        // per domain in principle but used as one (the disabled-users OU).
        services.AddSingleton(domains.Count > 0 ? domains[0] : new LdapOptions());
        services.AddSingleton<IDirectoryService>(sp => domains.Count switch
        {
            0 => new LdapDirectoryService(new LdapOptions(), sp.GetRequiredService<ILogger<LdapDirectoryService>>()),
            1 => new LdapDirectoryService(domains[0], sp.GetRequiredService<ILogger<LdapDirectoryService>>()),
            _ => new DirectoryRouter([.. domains.Select(d => (IDirectoryService)new LdapDirectoryService(d, sp.GetRequiredService<ILogger<LdapDirectoryService>>()))]),
        });

        services.AddSingleton<IAuthService>(sp => new AuthService(
            sp.GetRequiredService<AuthOptions>(),
            () => sp.GetRequiredService<WindowHandleProvider>().Handle,
            sp.GetRequiredService<ILogger<AuthService>>()));

        services.AddSingleton<GraphClient>();
        services.AddSingleton(sp => new ExchangeClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IAuthService>(),
            sp.GetRequiredService<TenantContext>(),
            sp.GetRequiredService<ILogger<ExchangeClient>>()));
        services.AddSingleton<ExchangeService>();
        services.AddSingleton<TenantContext>();
        services.AddSingleton<TenantContextLoader>();

        // Per-tenant services: the cache file is named by tenant id, so these can only be resolved after sign-in
        // (TenantContextLoader.LoadAsync). Resolving earlier is a programming error and throws.
        services.AddSingleton(sp =>
        {
            var tenant = sp.GetRequiredService<TenantContext>();
            if (!tenant.IsLoaded) throw new InvalidOperationException("CacheDb requested before the tenant context was loaded.");
            return CacheDb.OpenFile(AppPaths.CacheDbPath(tenant.TenantId), sp.GetRequiredService<ILogger<CacheDb>>());
        });
        services.AddSingleton<DeltaSyncService>();
        services.AddSingleton<ISyncStatus>(sp => sp.GetRequiredService<DeltaSyncService>());
        services.AddSingleton(sp => new SearchService(sp.GetRequiredService<CacheDb>(), PaletteActions.All));
        services.AddSingleton<UserService>();
        services.AddSingleton<DeviceService>();
        services.AddSingleton<GroupService>();
        services.AddSingleton<LicenseService>();
        services.AddSingleton<PermissionChecker>();

        // Offboarding (M6). The catalog is built per resolve because it holds the per-tenant services, which only
        // exist after sign-in; the runner itself is stateless.
        services.AddSingleton(sp => new OffboardingRunner(
            OffboardingCatalog.Build(
                sp.GetRequiredService<UserService>(),
                sp.GetRequiredService<GroupService>(),
                sp.GetRequiredService<LicenseService>(),
                sp.GetRequiredService<DeviceService>(),
                sp.GetRequiredService<GraphClient>(),
                sp.GetRequiredService<ActionLog>(),
                sp.GetRequiredService<TenantContext>(),
                sp.GetRequiredService<IDirectoryService>(),
                sp.GetRequiredService<LdapOptions>(),
                sp.GetRequiredService<ExchangeService>(),
                sp.GetRequiredService<CacheDb>(),
                sp.GetRequiredService<SnapshotService>(),
                sp.GetRequiredService<SnapshotStore>(),
                sp.GetRequiredService<OneDriveService>(),
                sp.GetRequiredService<OneDriveExporter>()),
            sp.GetRequiredService<ILogger<OffboardingRunner>>()));
        services.AddKeyedSingleton(UserDetailViewModel.RestoreRunnerKey, (sp, _) => new OffboardingRunner(
            RestoreCatalog.Build(
                sp.GetRequiredService<UserService>(),
                sp.GetRequiredService<GroupService>(),
                sp.GetRequiredService<LicenseService>(),
                sp.GetRequiredService<GraphClient>(),
                sp.GetRequiredService<ActionLog>(),
                sp.GetRequiredService<TenantContext>(),
                sp.GetRequiredService<IDirectoryService>(),
                sp.GetRequiredService<ExchangeService>(),
                sp.GetRequiredService<OneDriveService>()),
            sp.GetRequiredService<ILogger<OffboardingRunner>>()));
        services.AddSingleton(sp => new SnapshotService(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<CacheDb>(),
            sp.GetRequiredService<IDirectoryService>(),
            sp.GetRequiredService<ExchangeService>(),
            sp.GetRequiredService<ILogger<SnapshotService>>(),
            sp.GetRequiredService<OneDriveService>()));

        // OneDrive (M16, doc 14): SharePoint admin API plus Graph for the files.
        services.AddSingleton<SharePointClient>();
        services.AddSingleton<OneDriveService>();
        services.AddSingleton<OneDriveExporter>();
        services.AddSingleton<UserProvisioningService>();
        services.AddSingleton(sp => new TemplateService(
            TemplateService.DirectoryFor(sp.GetRequiredService<TenantContext>().TenantId, adoptShared: isPrimary),
            sp.GetRequiredService<GraphClient>(),
            sp.GetRequiredService<ILogger<TemplateService>>())
        {
            ActiveDirectory = sp.GetRequiredService<IDirectoryService>(),
            ExchangeService = sp.GetRequiredService<ExchangeService>(),
            Cache = sp.GetRequiredService<CacheDb>(),
        });

        // Hybrid onboarding (M17): create in AD, finish in the cloud after Entra Connect.
        services.AddSingleton<PendingProvisioningService>();
        services.AddSingleton<HybridProvisioningService>();
        services.AddSingleton<AdminConsole.Core.Users.BulkEdit.BulkEditService>();
        services.AddSingleton<AdminConsole.Core.Pim.PimService>();
        services.AddSingleton<AdminConsole.Core.Reports.MfaReportService>();
        services.AddSingleton<AdminConsole.Core.Reports.LicenceReportService>();
        services.AddSingleton<AdminConsole.Core.Reports.MailboxReportService>();
        services.AddSingleton<ConsentService>();
        services.AddTransient<PaletteViewModel>();

        // Page ViewModels are transient (doc 03): a fresh instance per navigation.
        services.AddTransient<HomePageViewModel>();
        services.AddTransient<PlaceholderPageViewModel>();
        services.AddTransient<UsersPageViewModel>();
        services.AddTransient<GroupsPageViewModel>();
        services.AddTransient<DevicesPageViewModel>();
        services.AddTransient<UserDetailViewModel>();
        services.AddTransient<MailboxTabViewModel>();
        services.AddTransient<OneDriveTabViewModel>();
        services.AddTransient<DeviceDetailViewModel>();
        services.AddTransient<GroupDetailViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<TemplatesPageViewModel>();
        services.AddTransient<ActivityPageViewModel>();
        services.AddTransient<ReportsPageViewModel>();
        services.AddTransient<MfaReportSection>();
        services.AddTransient<LicenceReportSection>();
        services.AddTransient<MailboxReportSection>();
        services.AddTransient<DomainPageViewModel>();
        services.AddTransient<DomainObjectViewModel>();
    }

    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled exception on the UI thread");
        e.Handled = true;
        ShowErrorBar(e.Exception, "Unexpected error");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
        ShowErrorBar(e.Exception.GetBaseException(), "Background operation failed");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Cannot be handled; log so the crash is never silent.
        _logger?.LogCritical(e.ExceptionObject as Exception, "Fatal unhandled exception (terminating={Terminating})", e.IsTerminating);
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.LogsDirectory, "crash.log"), $"{DateTimeOffset.Now:u} {e.ExceptionObject}{Environment.NewLine}");
        }
        catch
        {
            // nothing left to do
        }
    }

    private void ShowErrorBar(Exception ex, string context)
    {
        try
        {
            _host?.Services.GetRequiredService<ShellViewModel>().ShowError(ex, context);
        }
        catch (Exception inner)
        {
            _logger?.LogError(inner, "Could not display the error bar");
        }
    }
}
