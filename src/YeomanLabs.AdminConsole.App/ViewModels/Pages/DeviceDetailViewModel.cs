using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Devices;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Device page (UX §5), read-only in M4: header badges, Overview, People, Hardware, Security (reveal buttons greyed until
/// M5.6), Compliance per policy with failure reasons. Cache first, then one <c>$batch</c>.
/// </summary>
public sealed partial class DeviceDetailViewModel : ObservableObject, IPageViewModel, IRefreshable, IDisposable
{
    private readonly DeviceService _devices;
    private readonly TenantContext _tenant;
    private readonly ToastService _toasts;
    private readonly ILogger<DeviceDetailViewModel> _logger;
    private CancellationTokenSource? _cts;

    public DeviceDetailViewModel(DeviceService devices, TenantContext tenant, ToastService toasts, ILogger<DeviceDetailViewModel> logger)
    {
        _devices = devices;
        _tenant = tenant;
        _toasts = toasts;
        _logger = logger;
    }

    public string Id { get; private set; } = string.Empty;
    public string Title => Device?.DeviceName ?? Device?.SerialNumber ?? "Device";

    public Action<ObjectType, string, string>? OpenObject { get; set; }

    // ---- state ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    private ManagedDevice? _device;

    public bool HasDevice => Device is not null;

    [ObservableProperty]
    private EntraDevice? _entraDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComplianceLoaded))]
    [NotifyPropertyChangedFor(nameof(IsPeopleLoaded))]
    private DeviceLiveDetail? _live;

    public bool IsComplianceLoaded => Live is not null;
    public bool IsPeopleLoaded => Live is not null;

    [ObservableProperty]
    private bool _isHydrating;

    [ObservableProperty]
    private string? _hydrateError;

    [ObservableProperty]
    private string _timingText = string.Empty;

    public string ActionsDisabledReason => "Sync, Restart, Retire and Wipe arrive in M5.6";

    // ---- header ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<BadgeItem> _badges = [];

    [ObservableProperty]
    private string _subtitle = string.Empty;

    private void RecomputeHeader()
    {
        if (Device is null) return;
        Subtitle = string.Join(" · ", new[] { Device.Manufacturer, Device.Model }.Where(s => !string.IsNullOrEmpty(s)));
        var list = new List<BadgeItem>(6);
        list.Add(Device.ComplianceState switch
        {
            "compliant" => new BadgeItem("Compliant", BadgeTone.Good),
            "noncompliant" => new BadgeItem("Non-compliant", BadgeTone.Danger),
            "inGracePeriod" => new BadgeItem("Grace period", BadgeTone.Warning),
            "error" => new BadgeItem("Compliance error", BadgeTone.Danger),
            "conflict" => new BadgeItem("Conflict", BadgeTone.Warning),
            _ => new BadgeItem("Compliance unknown"),
        });
        list.Add(Device.IsEncrypted switch
        {
            true => new BadgeItem("Encrypted", BadgeTone.Good),
            false => new BadgeItem("Not encrypted", BadgeTone.Warning),
            null => new BadgeItem("Encryption unknown"),
        });
        if (Device.AutopilotEnrolled == true) list.Add(new BadgeItem("Autopilot", BadgeTone.Info, "Enrolled through an Autopilot profile"));
        if (IsCoManaged) list.Add(new BadgeItem("Co-managed", BadgeTone.Info, "Configuration Manager client with Intune co-management"));
        list.Add(Device.IsPersonal ? new BadgeItem("Personal", BadgeTone.Warning) : new BadgeItem("Corporate"));
        Badges = list;
    }

    private bool IsCoManaged
    {
        get
        {
            if (Device is null) return false;
            using var raw = Device.ParseRaw();
            var agent = raw.RootElement.GetString("managementAgent") ?? string.Empty;
            return agent.Contains("configurationManager", StringComparison.OrdinalIgnoreCase) && agent.Contains("Mdm", StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Overview -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<IdentityField> _overview = [];

    [ObservableProperty]
    private string? _entraObjectId;

    public bool HasEntraObject => EntraObjectId is not null;

    private void RecomputeOverview()
    {
        if (Device is null) return;
        using var raw = Device.ParseRaw();
        var e = raw.RootElement;
        EntraObjectId = EntraDevice?.Id;
        OnPropertyChanged(nameof(HasEntraObject));
        Overview =
        [
            new IdentityField("Operating system", "operatingSystem", string.Join(" ", new[] { Device.OperatingSystem, Device.OsVersion }.Where(s => !string.IsNullOrEmpty(s)))),
            new IdentityField("Enrolled", "enrolledDateTime", TimeText.Both(Device.EnrolledDateTime)),
            new IdentityField("Last check-in", "lastSyncDateTime", TimeText.Both(Device.LastSyncDateTime)),
            new IdentityField("Management agent", "managementAgent", e.GetString("managementAgent")),
            new IdentityField("Join type", "deviceEnrollmentType", Device.EnrollmentType),
            new IdentityField("Enrollment profile", "enrollmentProfileName", e.GetString("enrollmentProfileName")),
            new IdentityField("Ownership", "managedDeviceOwnerType", Device.OwnerType),
            new IdentityField("Device category", "deviceCategoryDisplayName", e.GetString("deviceCategoryDisplayName")),
            new IdentityField("Entra device ID", "azureADDeviceId", Device.AzureAdDeviceId, IsMono: true),
            new IdentityField("Entra object ID", "entraObjectId", EntraDevice?.Id, IsMono: true),
            new IdentityField("Entra trust type", "trustType", EntraDevice?.TrustType),
            new IdentityField("Intune device ID", "id", Device.Id, IsMono: true),
        ];
    }

    /// <summary>UX §5: link to the Entra device object. Opens the portal in the default browser; nothing leaves the app.</summary>
    [RelayCommand]
    private void OpenInEntraPortal()
    {
        if (EntraObjectId is null) return;
        var url = $"https://entra.microsoft.com/#view/Microsoft_AAD_Devices/DeviceDetailsMenuBlade/~/Properties/objectId/{EntraObjectId}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the browser");
            _toasts.Show("Could not open your browser");
        }
    }

    // ---- People ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private PersonRef? _primaryUser;

    [ObservableProperty]
    private IReadOnlyList<PersonRef> _otherUsers = [];

    [ObservableProperty]
    private string? _peopleCaption;

    [RelayCommand]
    private void OpenUser(PersonRef? person)
    {
        if (person is null) return;
        OpenObject?.Invoke(ObjectType.User, person.Id, person.Label);
    }

    private void RecomputePeople()
    {
        if (Device is null) return;
        var primaryId = Device.PrimaryUserId;
        if (Live?.Users.IsSuccess == true)
        {
            var users = Live.Users.Value!;
            PrimaryUser = users.FirstOrDefault(u => string.Equals(u.Id, primaryId, StringComparison.OrdinalIgnoreCase))
                          ?? (primaryId is null ? null : new PersonRef(primaryId, Device.PrimaryUserDisplayName, Device.PrimaryUserUpn));
            OtherUsers = users.Where(u => !string.Equals(u.Id, primaryId, StringComparison.OrdinalIgnoreCase)).ToList();
            PeopleCaption = PrimaryUser is null && OtherUsers.Count == 0 ? "No primary or registered users — a shared or kiosk device." : null;
            return;
        }

        PrimaryUser = primaryId is null ? null : new PersonRef(primaryId, Device.PrimaryUserDisplayName, Device.PrimaryUserUpn);
        OtherUsers = [];
        PeopleCaption = Live?.Users.Error is { } err ? $"Registered users unavailable ({err.ErrorCode}); showing the cached primary user." : null;
    }

    // ---- Hardware -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<IdentityField> _hardware = [];

    private void RecomputeHardware()
    {
        if (Device is null) return;
        using var raw = Device.ParseRaw();
        var e = raw.RootElement;
        static string? Gb(long? bytes) => bytes is null or 0 ? null : $"{bytes.Value / 1_073_741_824.0:F0} GB";
        var total = e.GetLong("totalStorageSpaceInBytes");
        var free = e.GetLong("freeStorageSpaceInBytes");
        var storage = Gb(total) is { } t ? (Gb(free) is { } f ? $"{f} free of {t}" : t) : null;
        Hardware =
        [
            new IdentityField("Manufacturer", "manufacturer", Device.Manufacturer),
            new IdentityField("Model", "model", Device.Model),
            new IdentityField("Serial number", "serialNumber", Device.SerialNumber, IsMono: true),
            new IdentityField("Storage", "totalStorageSpaceInBytes", storage),
            new IdentityField("Memory", "physicalMemoryInBytes", Gb(e.GetLong("physicalMemoryInBytes"))),
            new IdentityField("Wi-Fi MAC", "wiFiMacAddress", FormatMac(e.GetString("wiFiMacAddress")), IsMono: true),
            new IdentityField("Ethernet MAC", "ethernetMacAddress", FormatMac(e.GetString("ethernetMacAddress")), IsMono: true),
        ];
    }

    private static string? FormatMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        var clean = new string(mac.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return clean.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2))) : mac;
    }

    // ---- Security (UX §5; reveal wiring is M5.6) --------------------------------------------------------------------

    public string BitLockerReason => _tenant.HasScope(Scopes.BitLockerKeyReadAll)
        ? "Reveal arrives in M5.6"
        : $"Needs the '{Scopes.BitLockerKeyReadAll}' permission — click to grant";

    public string LapsReason => _tenant.HasScope(Scopes.DeviceLocalCredentialReadAll)
        ? "Reveal arrives in M5.6"
        : $"Needs the '{Scopes.DeviceLocalCredentialReadAll}' permission — click to grant";

    // ---- Compliance -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<CompliancePolicyState> _policies = [];

    [ObservableProperty]
    private string? _complianceCaption;

    private void RecomputeCompliance()
    {
        if (Live is null) return;
        if (Live.CompliancePolicies.IsSuccess)
        {
            Policies = Live.CompliancePolicies.Value!.OrderBy(p => p.IsCompliant || p.IsNotApplicable).ThenBy(p => p.Label).ToList();
            ComplianceCaption = Policies.Count == 0 ? "No compliance policies apply to this device." : null;
        }
        else
        {
            Policies = [];
            ComplianceCaption = Live.CompliancePolicies.IsForbidden
                ? "Needs the 'DeviceManagementConfiguration.Read.All' permission"
                : $"Could not read compliance policies ({Live.CompliancePolicies.Error!.ErrorCode})";
        }
    }

    // ---- copy -----------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _toasts.Show($"Copied {(text.Length <= 48 ? text : text[..45] + "…")}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard unavailable");
            _toasts.Show("Clipboard is unavailable right now");
        }
    }

    // ---- loading --------------------------------------------------------------------------------------------------

    public void Start(string id)
    {
        Id = id;
        _ = LoadAsync(CancellationToken.None);
    }

    public Task RefreshAsync(CancellationToken ct) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();

        var cached = await Task.Run(() =>
        {
            var d = _devices.GetCached(Id);
            return (d, _devices.GetCachedEntraDevice(d?.AzureAdDeviceId));
        }, cts.Token);
        if (cts.IsCancellationRequested) return;
        if (cached.d is not null) Device = cached.d;
        EntraDevice = cached.Item2;
        RecomputeAll();
        var cacheMs = sw.Elapsed.TotalMilliseconds;

        IsHydrating = true;
        HydrateError = null;
        try
        {
            var live = await _devices.HydrateAsync(Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (live.Device.Value is { } fresh) Device = fresh;
            else if (live.Device.Error is { } err) HydrateError = err.IsNotFound ? "This device no longer exists in Intune. The cache will update on the next refresh." : err.Message;
            Live = live;
            RecomputeAll();
            TimingText = $"cache {cacheMs:F0} ms · live {live.ElapsedMilliseconds:F0} ms";
            _logger.LogDebug("Device page {Id}: cache {CacheMs:F0} ms, live {LiveMs:F0} ms", Id, cacheMs, live.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // page closed or refreshed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device page {Id}: hydration failed", Id);
            HydrateError = ex is GraphException g ? g.Message : ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsHydrating = false;
        }
    }

    private void RecomputeAll()
    {
        RecomputeHeader();
        RecomputeOverview();
        RecomputePeople();
        RecomputeHardware();
        RecomputeCompliance();
    }

    public void Dispose() => _cts?.Cancel();
}
