using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Users;

/// <summary>
/// User 360 data access (doc 03 §Cache &amp; sync, doc 04 §Users/§Batching): the cache answers instantly, then one
/// <c>$batch</c> hydrates the live sections. Writes come in M5.
/// </summary>
public sealed partial class UserService
{
    private const string UserId = "user";
    private const string ManagerId = "manager";
    private const string MemberOfId = "memberOf";
    private const string AuthId = "auth";
    private const string DevicesId = "devices";
    private const string LicensesId = "licenses";
    private const string ReportsId = "reports";

    private readonly GraphClient _graph;
    private readonly CacheDb _db;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;
    private readonly ILogger<UserService> _logger;

    public UserService(GraphClient graph, CacheDb db, ActionLog actions, TenantContext tenant, ILogger<UserService> logger)
    {
        _graph = graph;
        _db = db;
        _actions = actions;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>Cache only; never touches Graph. Null when the user is not indexed yet.</summary>
    public DirectoryUser? GetCached(string id) => _db.GetUser(id);

    /// <summary>Cache only: Intune devices where the user is primary, and cached group memberships.</summary>
    public IReadOnlyList<ManagedDevice> GetCachedDevices(string userId) => _db.GetManagedDevicesForUser(userId);

    public IReadOnlyList<DirectoryGroup> GetCachedGroups(string userId) => _db.GetGroupsForMember(userId);

    /// <summary>
    /// One <c>$batch</c> for every live section. Inner failures are captured per section; a 403/400 on the user read
    /// (no P1 or no AuditLog.Read.All for <c>signInActivity</c>) triggers a second read without it.
    /// </summary>
    public async Task<UserLiveDetail> HydrateAsync(string id, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var responses = await _graph.BatchAsync(
        [
            BatchRequest.Get(UserId, $"/users/{id}?$select={GraphSelects.User}"),
            BatchRequest.Get(ManagerId, $"/users/{id}/manager?$select={GraphSelects.PersonRef}"),
            BatchRequest.Get(MemberOfId, $"/users/{id}/memberOf?$select={GraphSelects.MemberOf},roleTemplateId&$top=999"),
            BatchRequest.Get(AuthId, $"/users/{id}/authentication/methods"),
            BatchRequest.Get(DevicesId, $"/users/{id}/registeredDevices?$select={GraphSelects.Device}&$top=999"),
            BatchRequest.Get(LicensesId, $"/users/{id}?$select=id,licenseAssignmentStates"),
            BatchRequest.Get(ReportsId, $"/users/{id}/directReports?$select={GraphSelects.PersonRef}&$top=999"),
        ], ct).ConfigureAwait(false);

        var (user, signInUnavailable) = await ReadUserAsync(id, responses[UserId], ct).ConfigureAwait(false);
        var detail = new UserLiveDetail
        {
            UserId = id,
            User = user,
            SignInActivityUnavailable = signInUnavailable,
            SignInActivity = user.Value is { } u ? ReadSignInActivity(u) : null,
            Manager = ReadManager(responses[ManagerId]),
            Groups = Section<IReadOnlyList<DirectoryGroup>>(responses[MemberOfId], body => Items(body).Where(IsGroup).Select(DirectoryGroup.FromJson).ToList()),
            Roles = Section<IReadOnlyList<DirectoryRoleRef>>(responses[MemberOfId], body => Items(body).Where(IsRole).Select(ReadRole).ToList()),
            AuthMethods = Section<IReadOnlyList<AuthMethod>>(responses[AuthId], body => Items(body).Select(AuthMethod.FromJson).ToList()),
            RegisteredDevices = Section<IReadOnlyList<EntraDevice>>(responses[DevicesId], body => Items(body).Select(EntraDevice.FromJson).ToList()),
            LicenseStates = Section<IReadOnlyList<LicenseAssignmentState>>(responses[LicensesId], body => body is { } b && b.TryGetProperty("licenseAssignmentStates", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(LicenseAssignmentState.FromJson).ToList()
                : []),
            DirectReports = Section<IReadOnlyList<PersonRef>>(responses[ReportsId], body => Items(body).Select(PersonRef.FromJson).ToList()),
            ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
        };

        // Write-through (doc 03): the fresh user replaces the cached projection so the next open is current without a delta.
        if (user.Value is { } fresh)
        {
            _db.Write(w => w.UpsertUser(fresh));
        }

        _logger.LogInformation("Hydrated user {UserId} in {Ms:F0} ms (signInActivity {SignIn})", id, detail.ElapsedMilliseconds, signInUnavailable ? "unavailable" : "ok");
        return detail;
    }

    /// <summary>
    /// Doc 04: last 20 audit events targeting the user, loaded lazily on the Activity tab (2–5 s on big tenants).
    /// Needs AuditLog.Read.All; a 403 is returned as a failed section, not thrown.
    /// </summary>
    public async Task<SectionResult<IReadOnlyList<AuditEvent>>> GetAuditEventsAsync(string userId, CancellationToken ct)
    {
        var url = $"auditLogs/directoryAudits?$filter=targetResources/any(t:t/id eq '{userId}')&$top=20&$orderby=activityDateTime desc";
        try
        {
            var page = await _graph.GetPageAsync(url, ct).ConfigureAwait(false);
            return SectionResult<IReadOnlyList<AuditEvent>>.Ok(page.Items.Select(AuditEvent.FromJson).ToList());
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Audit events for {UserId}: HTTP {Status} {Code}", userId, ex.StatusCode, ex.ErrorCode);
            return SectionResult<IReadOnlyList<AuditEvent>>.Failed(ex);
        }
    }

    /// <summary>Live compliance/encryption/check-in for the user's cached Intune devices, one <c>$batch</c>. Missing devices (404) are simply absent.</summary>
    public async Task<IReadOnlyDictionary<string, ManagedDeviceState>> GetDeviceStatesAsync(IReadOnlyList<string> managedDeviceIds, CancellationToken ct)
    {
        var result = new Dictionary<string, ManagedDeviceState>(StringComparer.OrdinalIgnoreCase);
        if (managedDeviceIds.Count == 0) return result;

        var requests = managedDeviceIds.Select(id => BatchRequest.Get(id, $"/deviceManagement/managedDevices/{id}?$select={ManagedDeviceState.Select}")).ToList();
        var responses = await _graph.BatchAsync(requests, ct).ConfigureAwait(false);
        foreach (var (id, response) in responses)
        {
            if (response.IsSuccess && response.Body is { } body)
            {
                result[id] = ManagedDeviceState.FromJson(body);
            }
            else
            {
                _logger.LogDebug("Device state {DeviceId}: HTTP {Status}", id, response.Status);
            }
        }

        return result;
    }

    private async Task<(SectionResult<DirectoryUser> User, bool SignInUnavailable)> ReadUserAsync(string id, BatchResponse response, CancellationToken ct)
    {
        if (response.IsSuccess && response.Body is { } body)
        {
            return (SectionResult<DirectoryUser>.Ok(DirectoryUser.FromJson(body)), false);
        }

        // Doc 04: signInActivity needs AuditLog.Read.All + P1; on 403/400 retry without it.
        if (response.Status is 403 or 400)
        {
            _logger.LogInformation("User read with signInActivity returned {Status}; retrying without it", response.Status);
            try
            {
                using var doc = await _graph.SendAsync(HttpMethod.Get, $"users/{id}?$select={GraphSelects.UserDelta}", null, ct).ConfigureAwait(false);
                return doc is null
                    ? (SectionResult<DirectoryUser>.Failed(new GraphException(0, "EmptyResponse", "Graph returned an empty user.")), true)
                    : (SectionResult<DirectoryUser>.Ok(DirectoryUser.FromJson(doc.RootElement)), true);
            }
            catch (GraphException ex)
            {
                return (SectionResult<DirectoryUser>.Failed(ex), true);
            }
        }

        return (SectionResult<DirectoryUser>.Failed(GraphException.FromBatchResponse(response.Status, response.Body, response.Headers)), false);
    }

    private static SignInActivity? ReadSignInActivity(DirectoryUser user)
    {
        using var doc = user.ParseRaw();
        return SignInActivity.FromJson(doc.RootElement);
    }

    private static SectionResult<PersonRef?> ReadManager(BatchResponse response)
    {
        if (response.Status == 404) return SectionResult<PersonRef?>.Ok(null); // doc 04: 404 = no manager, not an error
        if (!response.IsSuccess) return SectionResult<PersonRef?>.Failed(GraphException.FromBatchResponse(response.Status, response.Body, response.Headers));
        return SectionResult<PersonRef?>.Ok(response.Body is { } b ? PersonRef.FromJson(b) : null);
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

    private static bool IsGroup(JsonElement e) => string.Equals(e.GetString("@odata.type"), "#microsoft.graph.group", StringComparison.OrdinalIgnoreCase);

    private static bool IsRole(JsonElement e) => string.Equals(e.GetString("@odata.type"), "#microsoft.graph.directoryRole", StringComparison.OrdinalIgnoreCase);

    private static DirectoryRoleRef ReadRole(JsonElement e)
    {
        var templateId = e.GetString("roleTemplateId") ?? e.GetString("id") ?? string.Empty;
        return DirectoryRoleRef.From(e.GetString("displayName"), templateId);
    }
}
