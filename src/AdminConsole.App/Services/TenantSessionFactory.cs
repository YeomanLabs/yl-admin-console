using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AdminConsole.App.ViewModels;
using AdminConsole.Core;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Reports;
using AdminConsole.Core.Sync;
using AdminConsole.Core.Templates;

namespace AdminConsole.App.Services;

/// <summary>
/// Builds a <see cref="TenantSession"/> (M20.2): a container holding everything that belongs to one tenant, with the
/// installation-wide services — the window, toasts, dialogs, the action log, the stores — passed through from the app's
/// own container so there is still exactly one of each.
/// </summary>
public sealed class TenantSessionFactory
{
    /// <summary>What every tenant shares. Listed rather than copied wholesale: copying would also copy multi-registrations
    /// (option configurators) whose meaning changes when resolved one at a time.</summary>
    private static readonly Type[] Shared =
    [
        typeof(ILoggerFactory), typeof(SyncOptions), typeof(BrandingStore), typeof(TenantConfigStore), typeof(WindowHandleProvider),
        typeof(HttpClient), typeof(ActionLog), typeof(SnapshotStore), typeof(UserSettingsStore), typeof(PendingProvisioningStore),
        typeof(ReportStore), typeof(OffboardingProfileStore), typeof(ToastService), typeof(DialogService), typeof(IDialogService),
        typeof(FilePicker), typeof(ShellViewModel),
    ];

    private readonly IServiceProvider _root;
    private readonly LdapOptions _appOnPrem;
    private readonly ILogger<TenantSessionFactory> _logger;

    public TenantSessionFactory(IServiceProvider root, LdapOptions appOnPrem, ILogger<TenantSessionFactory> logger)
    {
        _root = root;
        _appOnPrem = appOnPrem;
        _logger = logger;
    }

    /// <param name="isPrimary">The first profile: it also takes appsettings' <c>OnPrem</c> section, which predates profiles.</param>
    public TenantSession Create(TenantProfile profile, bool isPrimary)
    {
        var services = new ServiceCollection();
        foreach (var type in Shared) services.AddSingleton(type, _ => _root.GetRequiredService(type));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(profile);

        var domains = profile.Domains.Select(d => d.ToOptions()).ToList();
        if (domains.Count == 0 && isPrimary && (_appOnPrem.Domain.Length > 0 || _appOnPrem.Server.Length > 0)) domains.Add(_appOnPrem);
        App.ConfigureTenantServices(services, profile, domains, isPrimary);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });
        _logger.LogInformation("Opened a session for profile {Profile} ({Domains} on-prem domain(s))", profile.Id, domains.Count);
        return new TenantSession(profile, provider, _root.GetRequiredService<ILogger<TenantSession>>());
    }
}
