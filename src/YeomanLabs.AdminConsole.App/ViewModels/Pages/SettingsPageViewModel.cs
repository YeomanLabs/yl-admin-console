using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.Core;
using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Settings (UX §12). M5.1 builds the Permissions section — granted scopes with a Grant button for what is not consented
/// yet — plus read-only Account facts. Sync, Appearance and Diagnostics are completed in M8.1.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject, IPageViewModel
{
    private readonly ConsentService _consent;
    private readonly PermissionChecker _checker;
    private readonly TenantContext _tenant;
    private readonly ToastService _toasts;
    private readonly ILogger<SettingsPageViewModel> _logger;

    public SettingsPageViewModel(ConsentService consent, PermissionChecker checker, TenantContext tenant, ToastService toasts, ILogger<SettingsPageViewModel> logger)
    {
        _consent = consent;
        _checker = checker;
        _tenant = tenant;
        _toasts = toasts;
        _logger = logger;

        Permissions = Capabilities.All.Select(c => new PermissionRow(c, _checker.Check(c), GrantAsync)).ToList();
        _tenant.Changed += (_, _) => RefreshPermissions();
        Refresh();
    }

    public string Title => "Settings";

    // ---- Account (read-only here; sign-out and cache clearing arrive in M8.1) ---------------------------------------

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

    // ---- Diagnostics (paths only until M8.1) ------------------------------------------------------------------------

    public string LogsPath => AppPaths.LogsDirectory;
    public string CachePath => _tenant.IsLoaded ? AppPaths.CacheDbPath(_tenant.TenantId) : AppPaths.CacheDirectory;
    public string ActionsPath => AppPaths.ActionsDbPath;
    public string OtherSectionsNote => "Sync intervals, appearance, rebuild index and log export arrive in M8.1.";

    private void Refresh()
    {
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
