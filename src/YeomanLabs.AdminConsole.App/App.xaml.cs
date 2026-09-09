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
using YeomanLabs.AdminConsole.App.Views;
using YeomanLabs.AdminConsole.Core;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Search;
using YeomanLabs.AdminConsole.Core.Sync;

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

        // CLAUDE.md: apply the theme before the window is shown.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.None, false);

        var window = _host.Services.GetRequiredService<MainWindow>();
        var handles = _host.Services.GetRequiredService<WindowHandleProvider>();
        window.SourceInitialized += (_, _) => handles.Handle = new WindowInteropHelper(window).Handle;
        MainWindow = window;
        window.Show();
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
        services.AddSingleton(sp => new SearchService(sp.GetRequiredService<CacheDb>()));
        services.AddHostedService<SyncHostedService>();

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
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
