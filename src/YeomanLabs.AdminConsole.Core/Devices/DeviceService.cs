using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Devices;

/// <summary>
/// Device page data access (UX §5, doc 04 §Intune): cache first, then one <c>$batch</c> for the live device, registered
/// users and per-policy compliance; a second batch fetches setting states for the policies that failed. Actions come in M5.6.
/// </summary>
public sealed class DeviceService
{
    private const string DeviceId = "device";
    private const string UsersId = "users";
    private const string PoliciesId = "policies";

    private readonly GraphClient _graph;
    private readonly CacheDb _db;
    private readonly ILogger<DeviceService> _logger;

    public DeviceService(GraphClient graph, CacheDb db, ILogger<DeviceService> logger)
    {
        _graph = graph;
        _db = db;
        _logger = logger;
    }

    public ManagedDevice? GetCached(string id) => _db.GetManagedDevice(id);

    /// <summary>The Entra device object joined on <c>azureADDeviceId</c> (doc 05 §Object identity joins), from the cache.</summary>
    public EntraDevice? GetCachedEntraDevice(string? azureAdDeviceId) =>
        string.IsNullOrEmpty(azureAdDeviceId) ? null : _db.GetEntraDeviceByDeviceId(azureAdDeviceId);

    public DirectoryUser? GetCachedUser(string? userId) => string.IsNullOrEmpty(userId) ? null : _db.GetUser(userId);

    public async Task<DeviceLiveDetail> HydrateAsync(string id, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var responses = await _graph.BatchAsync(
        [
            BatchRequest.Get(DeviceId, $"/deviceManagement/managedDevices/{id}?$select={GraphSelects.ManagedDevice}"),
            BatchRequest.Get(UsersId, $"/deviceManagement/managedDevices/{id}/users?$select={GraphSelects.PersonRef}"),
            BatchRequest.Get(PoliciesId, $"/deviceManagement/managedDevices/{id}/deviceCompliancePolicyStates"),
        ], ct).ConfigureAwait(false);

        var device = Section(responses[DeviceId], body => body is { } b ? ManagedDevice.FromJson(b) : throw new JsonException("empty device"));
        var users = Section<IReadOnlyList<PersonRef>>(responses[UsersId], body => Items(body).Select(PersonRef.FromJson).ToList());
        var policies = Section<IReadOnlyList<CompliancePolicyState>>(responses[PoliciesId], body => Items(body).Select(CompliancePolicyState.FromJson).ToList());

        if (policies.Value is { } list && list.Any(p => p.NeedsReason))
        {
            policies = SectionResult<IReadOnlyList<CompliancePolicyState>>.Ok(await AttachReasonsAsync(id, list, ct).ConfigureAwait(false));
        }

        if (device.Value is { } fresh)
        {
            _db.Write(w => w.UpsertManagedDevice(fresh));
        }

        var detail = new DeviceLiveDetail
        {
            DeviceId = id,
            Device = device,
            Users = users,
            CompliancePolicies = policies,
            ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
        };
        _logger.LogInformation("Hydrated device {DeviceId} in {Ms:F0} ms", id, detail.ElapsedMilliseconds);
        return detail;
    }

    /// <summary>Doc 04: <c>/deviceCompliancePolicyStates/{id}/settingStates</c> for setting-level failure reasons; one batch for all failing policies.</summary>
    private async Task<IReadOnlyList<CompliancePolicyState>> AttachReasonsAsync(string deviceId, IReadOnlyList<CompliancePolicyState> policies, CancellationToken ct)
    {
        var failing = policies.Where(p => p.NeedsReason).ToList();
        var responses = await _graph.BatchAsync(
            failing.Select(p => BatchRequest.Get(p.Id, $"/deviceManagement/managedDevices/{deviceId}/deviceCompliancePolicyStates/{p.Id}/settingStates")).ToList(),
            ct).ConfigureAwait(false);

        return policies.Select(p =>
        {
            if (!p.NeedsReason || !responses.TryGetValue(p.Id, out var r) || !r.IsSuccess) return p;
            var settings = Items(r.Body).Select(ComplianceSettingState.FromJson).Where(s => !s.IsCompliant).ToList();
            return p with { SettingStates = settings };
        }).ToList();
    }

    private static SectionResult<T> Section<T>(BatchResponse response, Func<JsonElement?, T> read)
    {
        if (!response.IsSuccess) return SectionResult<T>.Failed(GraphException.FromBatchResponse(response.Status, response.Body, response.Headers));
        try
        {
            return SectionResult<T>.Ok(read(response.Body));
        }
        catch (JsonException ex)
        {
            return SectionResult<T>.Failed(new GraphException(response.Status, "ParseError", ex.Message, rawBody: response.Body?.GetRawText(), inner: ex));
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement? body) =>
        body is { } b && b.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}
