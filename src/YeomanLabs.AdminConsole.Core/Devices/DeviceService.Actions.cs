using System.Text.Json;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Writes;

namespace YeomanLabs.AdminConsole.Core.Devices;

/// <summary>
/// Device actions and secret reveals (UX §5, doc 04 §Intune/§Security). Nothing here is reversible: Sync and Restart
/// only cost the user time, Retire and Wipe do not. Every reveal is logged, and no secret ever reaches a log line.
/// </summary>
public sealed partial class DeviceService
{
    /// <summary>Asks the device to check in now. The safest of the four: it only pulls policy.</summary>
    public Task<WriteOutcome> SyncAsync(string deviceId, CancellationToken ct) =>
        PostActionAsync(deviceId, "syncDevice", "device.sync", name => $"Sync requested for {name}", null, ct);

    /// <summary>Reboots the device. The user loses unsaved work, so the UI confirms first.</summary>
    public Task<WriteOutcome> RestartAsync(string deviceId, CancellationToken ct) =>
        PostActionAsync(deviceId, "rebootNow", "device.restart", name => $"Restart sent to {name}", null, ct);

    /// <summary>
    /// Removes company data and unenrolls the device, leaving personal data alone. Not reversible: re-enrolling is the
    /// only way back, so the UI shows the preview dialog before calling this.
    /// </summary>
    public Task<WriteOutcome> RetireAsync(string deviceId, CancellationToken ct) =>
        PostActionAsync(deviceId, "retire", "device.retire", name => $"Retired {name}", null, ct);

    /// <summary>
    /// Factory-resets the device (doc 04). Not reversible and not recoverable, so the UI demands the device name typed
    /// out first. <paramref name="keepEnrollmentData"/> and <paramref name="keepUserData"/> default to a full wipe.
    /// </summary>
    public Task<WriteOutcome> WipeAsync(string deviceId, bool keepEnrollmentData, bool keepUserData, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["keepEnrollmentData"] = keepEnrollmentData,
            ["keepUserData"] = keepUserData,
        };

        return PostActionAsync(deviceId, "wipe", "device.wipe", name => $"Wipe sent to {name}", body, ct);
    }

    private async Task<WriteOutcome> PostActionAsync(string deviceId, string action, string logAction, Func<string, string> summarize, IReadOnlyDictionary<string, object?>? body, CancellationToken ct)
    {
        var device = _db.GetManagedDevice(deviceId);
        var label = device?.DeviceName ?? deviceId;
        var summary = summarize(label);
        var request = body is null
            ? $"POST {action}"
            : $"POST {action} {string.Join(" ", body.Select(kv => $"{kv.Key}={kv.Value}"))}";

        try
        {
            using var doc = await _graph.SendAsync(HttpMethod.Post, $"deviceManagement/managedDevices/{deviceId}/{action}", body, ct).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            var id = _actions.Record(NewAction(deviceId, label, logAction, request, ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("{Action} on {DeviceId} failed: {Code}", action, deviceId, ex.ErrorCode);
            return WriteOutcome.Failed(summary, ex, id, Capabilities.DeviceActions.RoleHint);
        }

        var actionId = _actions.Record(NewAction(deviceId, label, logAction, request, ActionOutcome.Success, summary));
        _logger.LogInformation("{Action} sent to {DeviceId}", action, deviceId);

        // Intune queues these; nothing to write through, and the next managedDevices refresh reflects the result.
        return new WriteOutcome { Success = true, Summary = summary, ActionId = actionId };
    }

    // ---- secrets (doc 04 §Security) ---------------------------------------------------------------------------------

    /// <summary>
    /// Lists the recovery key ids for a device, without any key material. Keyed on the Entra device id, not the Intune
    /// one (doc 05 §Object identity joins).
    /// </summary>
    public async Task<IReadOnlyList<BitLockerKeyRef>> GetBitLockerKeysAsync(string azureAdDeviceId, CancellationToken ct)
    {
        var url = $"informationProtection/bitlocker/recoveryKeys?$filter=deviceId eq '{Uri.EscapeDataString(azureAdDeviceId)}'&$select=id,volumeType,createdDateTime";
        var page = await _graph.GetPageAsync(url, ct).ConfigureAwait(false);
        return page.Items.Select(BitLockerKeyRef.FromJson).OrderBy(k => k.VolumeLabel).ThenBy(k => k.CreatedDateTime).ToList();
    }

    /// <summary>
    /// Reveals one recovery key. Microsoft audits this and so do we: the action log records which key was revealed for
    /// which device, never the key itself.
    /// </summary>
    public async Task<RevealResult<BitLockerKeyRef>> RevealBitLockerKeyAsync(string deviceId, string keyId, CancellationToken ct)
    {
        var device = _db.GetManagedDevice(deviceId);
        var label = device?.DeviceName ?? deviceId;
        var request = $"GET bitlocker/recoveryKeys/{keyId}";

        try
        {
            // $select=key MUST be the only select: naming any other property alongside it silently drops the key from
            // the response (verified on the dev tenant). Graph returns id, volumeType and createdDateTime regardless.
            using var doc = await _graph.SendAsync(HttpMethod.Get, $"informationProtection/bitlocker/recoveryKeys/{keyId}?$select=key", null, ct).ConfigureAwait(false)
                ?? throw new GraphException(0, "EmptyResponse", "Graph returned no body for a recovery key.");
            var key = BitLockerKeyRef.FromJson(doc.RootElement);
            _actions.Record(NewAction(deviceId, label, "device.bitlocker.reveal", request, ActionOutcome.Success, $"Revealed the {key.VolumeLabel} recovery key"));
            _logger.LogInformation("Revealed a BitLocker key for {DeviceId}", deviceId);
            return RevealResult<BitLockerKeyRef>.Ok(key);
        }
        catch (GraphException ex)
        {
            _actions.Record(NewAction(deviceId, label, "device.bitlocker.reveal", request, ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("BitLocker reveal for {DeviceId} failed: {Code}", deviceId, ex.ErrorCode);
            return RevealResult<BitLockerKeyRef>.Fail(GraphErrorText.Friendly(ex, Capabilities.RevealBitLocker.RoleHint), ex);
        }
    }

    /// <summary>
    /// Reveals the Windows LAPS local administrator password. Keyed on the Entra <c>deviceId</c> GUID — the directory
    /// object id answers 400 (doc 04 corrected). Logged like the BitLocker reveal, with the password kept out of the log.
    /// </summary>
    public async Task<RevealResult<IReadOnlyList<LapsCredential>>> RevealLapsAsync(string deviceId, string azureAdDeviceId, CancellationToken ct)
    {
        var device = _db.GetManagedDevice(deviceId);
        var label = device?.DeviceName ?? deviceId;
        var request = $"GET deviceLocalCredentials/{azureAdDeviceId}";

        try
        {
            // A device with no LAPS backup answers with an empty body rather than 404, so a null document is a normal
            // "nothing here", not an error.
            using var doc = await _graph.SendAsync(HttpMethod.Get, $"directory/deviceLocalCredentials/{azureAdDeviceId}?$select=credentials", null, ct).ConfigureAwait(false);

            var credentials = doc is not null && doc.RootElement.TryGetProperty("credentials", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(LapsCredential.FromJson).OrderByDescending(c => c.BackupDateTime).ToList()
                : [];

            if (credentials.Count == 0)
            {
                _actions.Record(NewAction(deviceId, label, "device.laps.reveal", request, ActionOutcome.Skipped, "No LAPS password is backed up for this device"));
                return RevealResult<IReadOnlyList<LapsCredential>>.Fail("No LAPS password is backed up for this device.", null);
            }

            _actions.Record(NewAction(deviceId, label, "device.laps.reveal", request, ActionOutcome.Success, $"Revealed the LAPS password for {credentials[0].AccountName}"));
            _logger.LogInformation("Revealed the LAPS password for {DeviceId}", deviceId);
            return RevealResult<IReadOnlyList<LapsCredential>>.Ok(credentials);
        }
        catch (GraphException ex)
        {
            _actions.Record(NewAction(deviceId, label, "device.laps.reveal", request, ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId));
            _logger.LogWarning("LAPS reveal for {DeviceId} failed: {Code}", deviceId, ex.ErrorCode);
            return RevealResult<IReadOnlyList<LapsCredential>>.Fail(GraphErrorText.Friendly(ex, Capabilities.RevealLaps.RoleHint), ex);
        }
    }

    private ActionRecord NewAction(string deviceId, string label, string action, string request, ActionOutcome outcome, string? message = null, string? requestId = null) => new()
    {
        Actor = _tenant.SignedInUpn,
        TenantId = _tenant.TenantId,
        TargetType = ObjectType.Device,
        TargetId = deviceId,
        TargetName = label,
        Action = action,
        RequestSummary = request,
        Outcome = outcome,
        Message = message,
        GraphRequestId = requestId,
    };
}
