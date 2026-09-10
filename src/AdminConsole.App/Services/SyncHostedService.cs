using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Sync;

namespace AdminConsole.App.Services;

/// <summary>
/// Hosts <see cref="DeltaSyncService.RunLoopAsync"/> (doc 03: the sync loop is a hosted background loop). It cannot start
/// until the tenant is known — the cache file is per tenant — so it waits for <see cref="TenantContext.IsLoaded"/> first.
/// </summary>
public sealed class SyncHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TenantContext _tenant;
    private readonly ILogger<SyncHostedService> _logger;

    public SyncHostedService(IServiceProvider services, TenantContext tenant, ILogger<SyncHostedService> logger)
    {
        _services = services;
        _tenant = tenant;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForTenantAsync(stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Tenant loaded; starting sync loop");
        var sync = _services.GetRequiredService<DeltaSyncService>();
        try
        {
            await sync.RunLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task WaitForTenantAsync(CancellationToken ct)
    {
        if (_tenant.IsLoaded) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? s, EventArgs e)
        {
            if (_tenant.IsLoaded) tcs.TrySetResult();
        }

        _tenant.Changed += OnChanged;
        try
        {
            if (_tenant.IsLoaded) return;
            await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _tenant.Changed -= OnChanged;
        }
    }
}
