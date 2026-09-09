using System.Text.Json;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Graph;

namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>
/// Fills <see cref="TenantContext"/> from one <c>$batch</c> (organization + me + my directory roles) plus the
/// granted scopes read from the cached token. Call after <see cref="IAuthService.EnsureSignedInAsync"/>.
/// </summary>
public sealed class TenantContextLoader
{
    private const string OrgId = "org";
    private const string MeId = "me";
    private const string RolesId = "roles";

    private readonly GraphClient _graph;
    private readonly IAuthService _auth;
    private readonly TenantContext _context;
    private readonly ILogger<TenantContextLoader> _logger;

    public TenantContextLoader(GraphClient graph, IAuthService auth, TenantContext context, ILogger<TenantContextLoader> logger)
    {
        _graph = graph;
        _auth = auth;
        _context = context;
        _logger = logger;
    }

    public async Task<TenantContext> LoadAsync(CancellationToken ct)
    {
        var scopesTask = _auth.GetGrantedScopesAsync(ct);
        var responses = await _graph.BatchAsync(
        [
            BatchRequest.Get(OrgId, $"/organization?$select={GraphSelects.Organization}"),
            BatchRequest.Get(MeId, $"/me?$select={GraphSelects.Me}"),
            BatchRequest.Get(RolesId, $"/me/memberOf/microsoft.graph.directoryRole?$select={GraphSelects.DirectoryRole}"),
        ], ct).ConfigureAwait(false);

        var org = FirstValue(responses[OrgId].EnsureSuccess())
            ?? throw new GraphException(0, "NoOrganization", "GET /organization returned no organization object.");
        var me = responses[MeId].EnsureSuccess()
            ?? throw new GraphException(0, "NoMe", "GET /me returned an empty body.");

        // Roles are a hint for the grey rule only; a 403 here must not block sign-in.
        var roles = new List<DirectoryRoleRef>();
        var rolesResponse = responses[RolesId];
        if (rolesResponse.IsSuccess && rolesResponse.Body is { } rolesBody &&
            rolesBody.TryGetProperty("value", out var roleItems) && roleItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var role in roleItems.EnumerateArray())
            {
                var templateId = GetString(role, "roleTemplateId");
                if (templateId is null) continue;
                // displayName is null when the token lacks Directory.Read.All; From() falls back to the local table.
                roles.Add(DirectoryRoleRef.From(GetString(role, "displayName"), templateId));
            }
        }
        else if (!rolesResponse.IsSuccess)
        {
            _logger.LogWarning("Could not read directory roles (HTTP {Status}); role hints disabled", rolesResponse.Status);
        }

        var (hasP1, hasP2) = ReadPremiumPlans(org);
        var domains = new List<string>();
        if (org.TryGetProperty("verifiedDomains", out var domainItems) && domainItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in domainItems.EnumerateArray())
            {
                var name = GetString(d, "name");
                if (name is not null) domains.Add(name);
            }
        }

        var snapshot = new TenantContextSnapshot
        {
            TenantId = GetString(org, "id") ?? string.Empty,
            TenantDisplayName = GetString(org, "displayName") ?? string.Empty,
            VerifiedDomains = domains,
            CountryLetterCode = GetString(org, "countryLetterCode"),
            OnPremisesSyncEnabled = org.TryGetProperty("onPremisesSyncEnabled", out var sync) && sync.ValueKind == JsonValueKind.True,
            OnPremisesLastSyncDateTime = GetDate(org, "onPremisesLastSyncDateTime"),
            SignedInUserId = GetString(me, "id") ?? string.Empty,
            SignedInUpn = GetString(me, "userPrincipalName") ?? string.Empty,
            SignedInDisplayName = GetString(me, "displayName") ?? string.Empty,
            GrantedScopes = await scopesTask.ConfigureAwait(false),
            Roles = roles,
            HasP1 = hasP1 || hasP2,
            HasP2 = hasP2,
        };

        _context.Update(snapshot);
        _logger.LogInformation("Tenant context loaded: tenant {TenantId}, user {UserId}, {RoleCount} role(s), {ScopeCount} scope(s), P1={HasP1}",
            snapshot.TenantId, snapshot.SignedInUserId, roles.Count, snapshot.GrantedScopes.Count, snapshot.HasP1);
        return _context;
    }

    private static (bool HasP1, bool HasP2) ReadPremiumPlans(JsonElement org)
    {
        bool p1 = false, p2 = false;
        if (!org.TryGetProperty("assignedPlans", out var plans) || plans.ValueKind != JsonValueKind.Array) return (false, false);
        foreach (var plan in plans.EnumerateArray())
        {
            if (!string.Equals(GetString(plan, "capabilityStatus"), "Enabled", StringComparison.OrdinalIgnoreCase)) continue;
            var id = GetString(plan, "servicePlanId");
            if (string.Equals(id, TenantContext.AadPremiumP1PlanId, StringComparison.OrdinalIgnoreCase)) p1 = true;
            if (string.Equals(id, TenantContext.AadPremiumP2PlanId, StringComparison.OrdinalIgnoreCase)) p2 = true;
        }

        return (p1, p2);
    }

    private static JsonElement? FirstValue(JsonElement? body)
    {
        if (body is not { } b) return null;
        if (b.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value.GetArrayLength() > 0 ? value[0] : null;
        }

        return b;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var dto)
            ? dto
            : null;
}
