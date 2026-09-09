using CommunityToolkit.Mvvm.ComponentModel;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One Intune device card on the Devices tab (UX §4 tab 3): built from the cache, then overlaid with the live
/// compliance/encryption/check-in slice once the batch returns.
/// </summary>
public sealed partial class DeviceCard : ObservableObject
{
    public DeviceCard(ManagedDevice device)
    {
        Device = device;
        ComplianceState = device.ComplianceState;
        IsEncrypted = device.IsEncrypted;
        LastSync = device.LastSyncDateTime;
    }

    public ManagedDevice Device { get; }
    public string Id => Device.Id;
    public string Name => Device.DeviceName ?? Device.SerialNumber ?? Device.Id;
    public string ModelText => string.Join(" ", new[] { Device.Manufacturer, Device.Model }.Where(s => !string.IsNullOrEmpty(s)));
    public string OsText => string.Join(" ", new[] { Device.OperatingSystem, Device.OsVersion }.Where(s => !string.IsNullOrEmpty(s)));
    public string? Serial => Device.SerialNumber;
    public bool IsAutopilot => Device.AutopilotEnrolled == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComplianceText))]
    [NotifyPropertyChangedFor(nameof(ComplianceTone))]
    private string? _complianceState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EncryptionText))]
    private bool? _isEncrypted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSyncText))]
    private DateTimeOffset? _lastSync;

    [ObservableProperty]
    private bool _isLive;

    public string ComplianceText => ComplianceState switch
    {
        "compliant" => "Compliant",
        "noncompliant" => "Non-compliant",
        "inGracePeriod" => "Grace period",
        "conflict" => "Conflict",
        "error" => "Error",
        "configManager" => "ConfigMgr",
        null or "" or "unknown" => "Unknown",
        var s => s,
    };

    public BadgeTone ComplianceTone => ComplianceState switch
    {
        "compliant" => BadgeTone.Good,
        "noncompliant" or "error" => BadgeTone.Danger,
        "inGracePeriod" or "conflict" => BadgeTone.Warning,
        _ => BadgeTone.Neutral,
    };

    public string EncryptionText => IsEncrypted switch { true => "Encrypted", false => "Not encrypted", null => "Encryption unknown" };

    public string LastSyncText => $"Checked in {TimeText.Relative(LastSync)}";

    public void ApplyLive(ManagedDeviceState state)
    {
        ComplianceState = state.ComplianceState;
        IsEncrypted = state.IsEncrypted;
        LastSync = state.LastSyncDateTime;
        IsLive = true;
    }
}
