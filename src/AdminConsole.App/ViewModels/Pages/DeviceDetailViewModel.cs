using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Devices;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Writes;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Device page (UX §5): header badges and actions, Overview, People, Hardware, Security (BitLocker and LAPS reveals),
/// Compliance per policy with failure reasons. Cache first, then one <c>$batch</c>.
/// </summary>
public sealed partial class DeviceDetailViewModel : ObservableObject, IPageViewModel, IRefreshable, IDisposable
{
    private readonly DeviceService _devices;
    private readonly TenantContext _tenant;
    private readonly PermissionChecker _permissions;
    private readonly ConsentService _consent;
    private readonly ToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly ILogger<DeviceDetailViewModel> _logger;
    private CancellationTokenSource? _cts;

    public DeviceDetailViewModel(DeviceService devices, TenantContext tenant, PermissionChecker permissions, ConsentService consent, ToastService toasts, IDialogService dialogs, ILogger<DeviceDetailViewModel> logger)
    {
        _devices = devices;
        _tenant = tenant;
        _permissions = permissions;
        _consent = consent;
        _tenant.Changed += (_, _) => RecomputeReasons();
        _toasts = toasts;
        _dialogs = dialogs;
        _logger = logger;
    }

    public string Id { get; private set; } = string.Empty;
    public string Title => Device?.DeviceName ?? Device?.SerialNumber ?? "Device";

    public Action<ObjectType, string, string>? OpenObject { get; set; }

    // ---- state ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    [NotifyPropertyChangedFor(nameof(CanRunActions))]
    [NotifyPropertyChangedFor(nameof(CanRevealBitLocker))]
    [NotifyPropertyChangedFor(nameof(CanRevealLaps))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunActions))]
    private string? _actionsDisabledReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevealBitLocker))]
    private string? _bitLockerReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevealLaps))]
    private string? _lapsReason;

    /// <summary>A device action is running; the header buttons stay disabled until it comes back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunActions))]
    [NotifyPropertyChangedFor(nameof(CanRevealBitLocker))]
    [NotifyPropertyChangedFor(nameof(CanRevealLaps))]
    private bool _isActionRunning;

    /// <summary>A permission not yet consented does not disable these: each asks for consent on the click (EnsureAsync).</summary>
    public bool CanRunActions => HasDevice && !IsActionRunning;

    /// <summary>Windows only: neither secret exists on any other platform, and offering the button would mislead.</summary>
    public bool CanRevealBitLocker => IsWindows && !IsActionRunning;

    public bool CanRevealLaps => IsWindows && !IsActionRunning;

    private bool IsWindows => Device?.OperatingSystem?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>UX §6: consent first, then "not built yet". Recomputed whenever the granted scopes change.</summary>
    private void RecomputeReasons()
    {
        ActionsDisabledReason = GreyRule.Reason(_permissions, Capabilities.DeviceActions, null);
        var notWindows = IsWindows ? null : "Only Windows devices have BitLocker keys and a LAPS password";
        BitLockerReason = notWindows ?? GreyRule.Reason(_permissions, Capabilities.RevealBitLocker, null);
        LapsReason = notWindows ?? GreyRule.Reason(_permissions, Capabilities.RevealLaps, null);
    }

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

    // ---- actions and secrets (UX 5) -------------------------------------------------------------------------------

    private string DeviceLabel => Device?.DeviceName ?? Device?.SerialNumber ?? Id;

    /// <summary>Pull policy now. The one action with no confirm: it costs the user nothing.</summary>
    [RelayCommand]
    private Task SyncAsync() => RunActionAsync(Capabilities.DeviceActions, ct => _devices.SyncAsync(Id, ct));

    /// <summary>Restart. Confirmed because the user loses unsaved work.</summary>
    [RelayCommand]
    private async Task RestartAsync()
    {
        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Restart {DeviceLabel}?",
            Message = "The device restarts as soon as it next checks in. Anyone using it loses unsaved work.",
            ConfirmLabel = "Restart",
            Destructive = true,
        });

        if (confirmed) await RunActionAsync(Capabilities.DeviceActions, ct => _devices.RestartAsync(Id, ct));
    }

    /// <summary>
    /// Retire (UX 5): the preview dialog spells out what leaves the device and what stays, because the wording is the
    /// only thing standing between the admin and an unenrolled device.
    /// </summary>
    [RelayCommand]
    private async Task RetireAsync()
    {
        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Retire {DeviceLabel}?",
            Message = "Retiring removes company data and unenrolls the device from Intune. It cannot be undone, and the device has to enrol again.",
            Bullets =
            [
                "Company apps, profiles and certificates are removed",
                "Company email accounts are removed",
                "Personal data, apps and settings are left alone",
                "The device disappears from Intune once it reports back",
            ],
            ConfirmLabel = "Retire device",
            Destructive = true,
        });

        if (confirmed) await RunActionAsync(Capabilities.DeviceActions, ct => _devices.RetireAsync(Id, ct));
    }

    /// <summary>
    /// Wipe (UX 5): a factory reset, so the admin types the device name before the button will fire. Nothing on the
    /// device survives and there is no undo.
    /// </summary>
    [RelayCommand]
    private async Task WipeAsync()
    {
        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Wipe {DeviceLabel}?",
            Message = "A wipe factory-resets the device. Everything on it is erased, including personal data, and it cannot be undone.",
            Bullets =
            [
                "The disk is erased and the device is reset to its factory state",
                "Nothing is recoverable afterwards, by anyone",
                "Retire is the gentler option if you only need company data removed",
            ],
            RequiredTypedValue = DeviceLabel,
            ConfirmLabel = "Wipe device",
            Destructive = true,
        });

        if (confirmed) await RunActionAsync(Capabilities.DeviceActions, ct => _devices.WipeAsync(Id, keepEnrollmentData: false, keepUserData: false, ct));
    }

    private async Task RunActionAsync(Capability capability, Func<CancellationToken, Task<WriteOutcome>> run)
    {
        if (!await EnsureAsync(capability)) return;
        IsActionRunning = true;
        try
        {
            var outcome = await run(CancellationToken.None);
            _toasts.Show(outcome.Success ? outcome.Summary : outcome.FriendlyError ?? outcome.Summary);
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    /// <summary>
    /// BitLocker reveal (UX 5). Lists the device keys, then reveals each one; every reveal is logged separately by the
    /// service. The keys are shown in a dialog and never touch the cache.
    /// </summary>
    [RelayCommand]
    private async Task RevealBitLockerAsync()
    {
        if (Device?.AzureAdDeviceId is not { } aadDeviceId)
        {
            _toasts.Show("This device has no Entra device ID, so its recovery keys cannot be looked up.");
            return;
        }

        if (!await EnsureAsync(Capabilities.RevealBitLocker)) return;

        IsActionRunning = true;
        try
        {
            var keys = await _devices.GetBitLockerKeysAsync(aadDeviceId, CancellationToken.None);
            if (keys.Count == 0)
            {
                _toasts.Show("No BitLocker recovery keys are escrowed for this device.");
                return;
            }

            var rows = new List<SecretRow>(keys.Count);
            foreach (var key in keys)
            {
                var revealed = await _devices.RevealBitLockerKeyAsync(Id, key.Id, CancellationToken.None);
                if (!revealed.Success)
                {
                    _toasts.Show(revealed.FriendlyError ?? "The recovery key could not be revealed.");
                    return;
                }

                rows.Add(new SecretRow(revealed.Value!.VolumeLabel, revealed.Value.Key ?? string.Empty, $"Escrowed {TimeText.Both(key.CreatedDateTime)}"));
            }

            await _dialogs.ShowAsync(new SecretRevealDialogViewModel($"BitLocker recovery keys - {DeviceLabel}", rows)
            {
                Message = "Every reveal is recorded in the activity log, here and in Microsoft's own audit log.",
            });
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("BitLocker key list for {DeviceId} failed: {Code}", Id, ex.ErrorCode);
            _toasts.Show(GraphErrorText.Friendly(ex, Capabilities.RevealBitLocker.RoleHint));
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    /// <summary>
    /// LAPS reveal (UX 5). Keyed on the Entra device ID, not the directory object ID, which answers 400 (verified on
    /// the dev tenant; doc 04 corrected).
    /// </summary>
    [RelayCommand]
    private async Task RevealLapsAsync()
    {
        if (Device?.AzureAdDeviceId is not { } aadDeviceId)
        {
            _toasts.Show("This device has no Entra device ID, so its LAPS password cannot be looked up.");
            return;
        }

        if (!await EnsureAsync(Capabilities.RevealLaps)) return;

        IsActionRunning = true;
        try
        {
            var revealed = await _devices.RevealLapsAsync(Id, aadDeviceId, CancellationToken.None);
            if (!revealed.Success)
            {
                _toasts.Show(revealed.FriendlyError ?? "The LAPS password could not be revealed.");
                return;
            }

            var rows = revealed.Value!
                .Select(c => new SecretRow(c.AccountName, c.Password, $"Backed up {TimeText.Both(c.BackupDateTime)}"))
                .ToList();

            await _dialogs.ShowAsync(new SecretRevealDialogViewModel($"LAPS password - {DeviceLabel}", rows)
            {
                Message = "Every reveal is recorded in the activity log. The device rotates this password after it is used.",
            });
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    /// <summary>Consent on the first use of a capability; false means the admin declined or lacks the scope.</summary>
    private async Task<bool> EnsureAsync(Capability capability)
    {
        var permission = _permissions.Check(capability);
        if (!permission.NeedsConsent) return true;
        try
        {
            permission = await _consent.EnsureAsync(capability, CancellationToken.None);
        }
        catch (SignInException ex)
        {
            await ConsentFailure.ShowAsync(_dialogs, _toasts, ex);
            return false;
        }

        if (!permission.CanRun) _toasts.Show(permission.Reason ?? "Not permitted.");
        return permission.CanRun;
    }

    // ---- loading --------------------------------------------------------------------------------------------------

    public void Start(string id)
    {
        Id = id;
        RecomputeReasons();
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
        // The Windows-only rule for the two reveals needs the loaded device, so the reasons are recomputed here too.
        RecomputeReasons();
        RecomputeHeader();
        RecomputeOverview();
        RecomputePeople();
        RecomputeHardware();
        RecomputeCompliance();
    }

    public void Dispose() => _cts?.Cancel();
}
