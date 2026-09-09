using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Devices;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 8. Off by default: plenty of organisations reassign the hardware instead, and retiring it first only
/// makes that slower. Not reversible — the device has to enrol again.
/// </summary>
public sealed class RetireDevicesStep : IOffboardingStep
{
    private const string ScratchKey = "devices-to-retire";

    private readonly DeviceService _devices;
    private readonly GraphClient _graph;

    public RetireDevicesStep(DeviceService devices, GraphClient graph)
    {
        _devices = devices;
        _graph = graph;
    }

    public string Id => "retire-devices";
    public string Title => "Retire Intune devices";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.DeviceActions;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var devices = await ReadDevicesAsync(ctx.UserId, ct).ConfigureAwait(false);
        ctx.Set(ScratchKey, devices.ToList());

        if (devices.Count == 0) return StepPreview.NoOp(Id, "No Intune devices have this user as their primary user");

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Retire {devices.Count} device{(devices.Count == 1 ? string.Empty : "s")}",
            Details = devices.Select(Describe).ToList(),
            Reason = "Retiring removes company data and unenrols the device. It cannot be undone — the device must enrol again.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var devices = ctx.Get<List<ManagedDevice>>(ScratchKey) ?? [];
        var retired = new List<string>();
        var failed = new List<string>();

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _devices.RetireAsync(device.Id, ct).ConfigureAwait(false);
            if (outcome.Success) retired.Add(device.DeviceName ?? device.Id);
            else failed.Add($"{device.DeviceName ?? device.Id} ({outcome.FriendlyError})");
        }

        if (retired.Count == 0 && failed.Count > 0) return StepResult.Failed(Id, $"Could not retire any device: {string.Join("; ", failed)}");

        var message = $"Retired {retired.Count} device{(retired.Count == 1 ? string.Empty : "s")}: {string.Join(", ", retired)}";
        return failed.Count > 0
            ? StepResult.Failed(Id, $"{message}, but {failed.Count} failed: {string.Join("; ", failed)}")
            : StepResult.Success(Id, message);
    }

    /// <summary>Live: a device enrolled since the last managedDevices refresh must not be missed.</summary>
    private async Task<IReadOnlyList<ManagedDevice>> ReadDevicesAsync(string userId, CancellationToken ct)
    {
        var page = await _graph.GetPageAsync($"deviceManagement/managedDevices?$filter=userId eq '{userId}'&$select={GraphSelects.ManagedDevice}", ct).ConfigureAwait(false);
        return page.Items.Select(ManagedDevice.FromJson).ToList();
    }

    private static string Describe(ManagedDevice d)
    {
        var parts = new List<string>(3);
        if (d.Model is { Length: > 0 }) parts.Add(d.Model);
        if (d.OperatingSystem is { Length: > 0 }) parts.Add(d.OperatingSystem);
        if (d.LastSyncDateTime is { } sync) parts.Add($"last check-in {Age(sync)}");
        return parts.Count == 0 ? d.DeviceName ?? d.Id : $"{d.DeviceName ?? d.Id} ({string.Join(", ", parts)})";
    }

    private static string Age(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        if (age < TimeSpan.FromHours(1)) return "under an hour ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} hours ago";
        return $"{(int)age.TotalDays} days ago";
    }
}
