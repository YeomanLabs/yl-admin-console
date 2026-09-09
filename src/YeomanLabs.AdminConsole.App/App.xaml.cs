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
using YeomanLabs.AdminConsole.App.Logging;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.App.ViewModels;
using YeomanLabs.AdminConsole.App.ViewModels.Pages;
using YeomanLabs.AdminConsole.App.ViewModels.Palette;
using YeomanLabs.AdminConsole.App.Views;
using YeomanLabs.AdminConsole.Core;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Devices;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Licensing;
using YeomanLabs.AdminConsole.Core.Groups;
using YeomanLabs.AdminConsole.Core.Search;
using YeomanLabs.AdminConsole.Core.Sync;
using YeomanLabs.AdminConsole.Core.Offboarding;
using YeomanLabs.AdminConsole.Core.Offboarding.Steps;
using YeomanLabs.AdminConsole.Core.Templates;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.App;

/// <summary>Composition root (doc 03 §Dependency injection). Builds the host, wires DI and logging, owns the global exception handlers.</summary>
public sealed partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
                logging.AddFilter("YeomanLabs", LogLevel.Debug);
                logging.AddProvider(new FileLoggerProvider(AppPaths.LogsDirectory));
            })
            .ConfigureServices(ConfigureServices)
            .Build();

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        HookGlobalExceptionHandlers();

        await _host.StartAsync();
        _logger.LogInformation("Admin Console {Version} starting", typeof(App).Assembly.GetName().Version);

        // CLAUDE.md: apply the theme before the window is shown. UX §1: coral is the accent for primary actions and focus.
        // The saved appearance, before any window exists: applying it later would flash the wrong theme.
        var settings = _host.Services.GetRequiredService<UserSettingsStore>().Load();
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.None, false);
        AppearanceService.ApplyAll(settings);

        // The sync loop reads its intervals each cycle, so the saved choice takes effect from the first one.
        var syncOptions = _host.Services.GetRequiredService<SyncOptions>();
        syncOptions.DeltaIntervalSeconds = settings.DeltaIntervalSeconds;
        syncOptions.DeviceRefreshIntervalSeconds = settings.DeviceRefreshIntervalSeconds;
        ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(0xFF, 0x69, 0x50), ApplicationTheme.Dark, false);

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
        services.AddSingleton(ctx.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions());
        services.AddSingleton(ctx.Configuration.GetSection(SyncOptions.SectionName).Get<SyncOptions>() ?? new SyncOptions());

        services.AddSingleton<WindowHandleProvider>();
        services.AddSingleton<IAuthService>(sp => new AuthService(
            sp.GetRequiredService<AuthOptions>(),
            () => sp.GetRequiredService<WindowHandleProvider>().Handle,
            sp.GetRequiredService<ILogger<AuthService>>()));

        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));
        services.AddSingleton<GraphClient>();
        services.AddSingleton<TenantContext>();
        services.AddSingleton<TenantContextLoader>();
        services.AddSingleton(sp => ActionLog.OpenFile(AppPaths.ActionsDbPath, sp.GetRequiredService<ILogger<ActionLog>>()));

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
                sp.GetRequiredService<TenantContext>()),
            sp.GetRequiredService<ILogger<OffboardingRunner>>()));
        services.AddSingleton(sp => new UserSettingsStore(UserSettingsStore.DefaultPath, sp.GetRequiredService<ILogger<UserSettingsStore>>()));
        services.AddSingleton<UserProvisioningService>();
        services.AddSingleton(sp => new TemplateService(
            TemplateService.DefaultDirectory,
            sp.GetRequiredService<GraphClient>(),
            sp.GetRequiredService<ILogger<TemplateService>>()));
        services.AddSingleton(sp => new OffboardingProfileStore(
            OffboardingProfileStore.DefaultDirectory,
            sp.GetRequiredService<ILogger<OffboardingProfileStore>>()));
        services.AddSingleton<ConsentService>();
        services.AddHostedService<SyncHostedService>();

        services.AddSingleton<ToastService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<PaletteViewModel>();

        // Page ViewModels are transient (doc 03): a fresh instance per navigation.
        services.AddTransient<HomePageViewModel>();
        services.AddTransient<PlaceholderPageViewModel>();
        services.AddTransient<UserDetailViewModel>();
        services.AddTransient<DeviceDetailViewModel>();
        services.AddTransient<GroupDetailViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<TemplatesPageViewModel>();
        services.AddTransient<ActivityPageViewModel>();
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
