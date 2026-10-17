using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AdminConsole.App.ViewModels.Palette;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Sync;
using AdminConsole.Core.Templates;

namespace AdminConsole.App.Services;

/// <summary>
/// One tenant open in the console (M20.2): its own sign-in, cache, sync loop and services, in a container of its own.
/// Every tab belongs to one session, so a page opened in a tab talks to that tab's tenant and no other.
/// </summary>
public sealed class TenantSession : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _syncLoop;
    private PaletteViewModel? _palette;

    public TenantSession(TenantProfile profile, ServiceProvider services, ILogger logger)
    {
        Profile = profile;
        _services = services;
        _logger = logger;
    }

    /// <summary>The profile as last saved; updated once the sign-in says which tenant and account this really is.</summary>
    public TenantProfile Profile { get; set; }

    public IServiceProvider Services => _services;

    public TenantContext Tenant => _services.GetRequiredService<TenantContext>();

    public IAuthService Auth => _services.GetRequiredService<IAuthService>();

    public TenantContextLoader Loader => _services.GetRequiredService<TenantContextLoader>();

    /// <summary>Null until the tenant is loaded: the sync service needs the per-tenant cache.</summary>
    public ISyncStatus? Sync { get; private set; }

    /// <summary>The palette searches this tenant's cache; one per session, made when the session starts.</summary>
    public PaletteViewModel Palette => _palette ??= _services.GetRequiredService<PaletteViewModel>();

    /// <summary>What a tab badge says: the tenant's name, short.</summary>
    public string Label => Tenant.IsLoaded && Tenant.TenantDisplayName.Length > 0 ? Tenant.TenantDisplayName : Profile.DisplayName;

    public bool IsStarted => _syncLoop is not null;

    /// <summary>
    /// Starts this tenant's sync loop (what <c>SyncHostedService</c> did for the one tenant there used to be), and the
    /// hybrid queue that moves on after each cycle (M17.3). Call once the tenant context is loaded.
    /// </summary>
    public void Start()
    {
        if (_syncLoop is not null) return;
        var sync = _services.GetRequiredService<DeltaSyncService>();
        Sync = sync;
        var pending = _services.GetRequiredService<PendingProvisioningService>();
        var ct = _stop.Token;
        sync.CycleCompleted += (_, _) => _ = Task.Run(async () =>
        {
            try
            {
                await pending.ProcessAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Processing pending hybrid creates failed");
            }
        }, ct);

        _logger.LogInformation("Starting the sync loop for tenant {TenantId}", Tenant.TenantId);
        _syncLoop = Task.Run(async () =>
        {
            try
            {
                await sync.RunLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // closed
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The sync loop for tenant {TenantId} stopped", Tenant.TenantId);
            }
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_syncLoop is not null)
        {
            try
            {
                await _syncLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("The sync loop did not stop within 5 s");
            }
        }

        await _services.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
