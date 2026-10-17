using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Pim;

/// <summary>
/// The signed-in admin's own Privileged Identity Management (M18.2): which roles they are eligible for, what each
/// role's policy demands at activation, and self-activation.
/// </summary>
/// <remarks>
/// <para>
/// Only the admin's own roles, through <c>filterByCurrentUser</c>: this is "let me do my job", not PIM
/// administration, so it needs no role of its own and asks for nothing an ordinary eligible admin cannot consent to.
/// </para>
/// <para>
/// PIM needs Entra ID P2 or ID Governance. Without it Graph answers the eligibility read with an error rather than an
/// empty list, and that error is the honest answer to show: "this tenant has no PIM", not "you are eligible for
/// nothing".
/// </para>
/// </remarks>
public sealed class PimService
{
    private const string Expand = "$expand=roleDefinition($select=id,displayName,templateId)";

    public const string NoLicence =
        "Privileged Identity Management needs Entra ID P2 or ID Governance, and this tenant's licence does not include it " +
        "(or it is suspended). Roles here are permanent assignments only.";

    private readonly GraphClient _graph;
    private readonly TenantContext _tenant;
    private readonly ActionLog _actions;
    private readonly ILogger<PimService> _logger;

    public PimService(GraphClient graph, TenantContext tenant, ActionLog actions, ILogger<PimService> logger)
    {
        _graph = graph;
        _tenant = tenant;
        _actions = actions;
        _logger = logger;
    }

    /// <summary>Eligible and active roles, merged per role. Also tells <see cref="TenantContext"/> what is eligible, so the grey rule can say so.</summary>
    public async Task<PimRead> GetMyRolesAsync(CancellationToken ct)
    {
        JsonDocument? eligibleDoc;
        try
        {
            eligibleDoc = await _graph.SendAsync(HttpMethod.Get,
                $"roleManagement/directory/roleEligibilityScheduleInstances/filterByCurrentUser(on='principal')?{Expand}", null, ct).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Reading PIM eligibility failed: {Status} {Code}", ex.StatusCode, ex.ErrorCode);
            return PimRead.Fail(Unavailable(ex));
        }

        var roles = new Dictionary<string, PimRole>(StringComparer.OrdinalIgnoreCase);
        using (eligibleDoc)
        {
            foreach (var item in Values(eligibleDoc))
            {
                var role = Role(item);
                if (role is null) continue;
                roles[role.RoleDefinitionId] = role with
                {
                    EligibleUntil = Date(item, "endDateTime"),
                    ViaGroup = string.Equals(Text(item, "memberType"), "Group", StringComparison.OrdinalIgnoreCase),
                };
            }
        }

        try
        {
            using var activeDoc = await _graph.SendAsync(HttpMethod.Get,
                $"roleManagement/directory/roleAssignmentScheduleInstances/filterByCurrentUser(on='principal')?{Expand}", null, ct).ConfigureAwait(false);
            foreach (var item in Values(activeDoc))
            {
                var role = Role(item);
                if (role is null) continue;
                var activated = string.Equals(Text(item, "assignmentType"), "Activated", StringComparison.OrdinalIgnoreCase);
                var until = Date(item, "endDateTime");
                roles[role.RoleDefinitionId] = (roles.TryGetValue(role.RoleDefinitionId, out var known) ? known : role) with
                {
                    IsActive = true,
                    ActiveUntil = until,
                    IsPermanent = !activated && until is null,
                };
            }
        }
        catch (GraphException ex)
        {
            // Eligibility read fine and this did not: show the eligible roles without claiming any is inactive.
            _logger.LogWarning("Reading active PIM roles failed: {Status} {Code}", ex.StatusCode, ex.ErrorCode);
        }

        var list = roles.Values.OrderBy(r => r.IsPermanent).ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var active = _tenant.Roles
            .Concat(list.Where(r => r.IsActive).Select(r => DirectoryRoleRef.From(r.DisplayName, r.RoleTemplateId)))
            .DistinctBy(r => r.RoleTemplateId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var eligible = list.Where(r => !r.IsActive).Select(r => DirectoryRoleRef.From(r.DisplayName, r.RoleTemplateId)).ToList();
        _tenant.UpdateRoles(active, eligible);
        return new PimRead(list, null);
    }

    /// <summary>The role's activation rules. Falls back to Entra's defaults, marked as assumed, when the policy cannot be read.</summary>
    public async Task<PimPolicy> GetPolicyAsync(string roleDefinitionId, CancellationToken ct)
    {
        try
        {
            var filter = Uri.EscapeDataString($"scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '{roleDefinitionId}'");
            using var doc = await _graph.SendAsync(HttpMethod.Get,
                $"policies/roleManagementPolicyAssignments?$filter={filter}&$expand=policy($expand=rules)", null, ct).ConfigureAwait(false);
            var assignment = Values(doc).FirstOrDefault();
            if (assignment.ValueKind != JsonValueKind.Object || !assignment.TryGetProperty("policy", out var policy)
                || !policy.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            {
                return PimPolicy.Default;
            }

            return ParsePolicy(rules);
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Reading the PIM policy for {Role} failed: {Status} {Code}", roleDefinitionId, ex.StatusCode, ex.ErrorCode);
            return PimPolicy.Default;
        }
    }

    internal static PimPolicy ParsePolicy(JsonElement rules)
    {
        var max = TimeSpan.FromHours(8);
        bool justification = false, ticket = false, mfa = false, approval = false;
        foreach (var rule in rules.EnumerateArray())
        {
            switch (Text(rule, "id"))
            {
                case "Expiration_EndUser_Assignment":
                    if (Text(rule, "maximumDuration") is { } iso)
                    {
                        try { max = XmlConvert.ToTimeSpan(iso); }
                        catch (FormatException) { /* keep the default */ }
                    }

                    break;
                case "Enablement_EndUser_Assignment":
                    if (rule.TryGetProperty("enabledRules", out var enabled) && enabled.ValueKind == JsonValueKind.Array)
                    {
                        var set = enabled.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        justification = set.Contains("Justification");
                        ticket = set.Contains("Ticketing");
                        mfa = set.Contains("MultiFactorAuthentication");
                    }

                    break;
                case "Approval_EndUser_Assignment":
                    approval = rule.TryGetProperty("setting", out var setting) && setting.TryGetProperty("isApprovalRequired", out var req) && req.ValueKind == JsonValueKind.True;
                    break;
            }
        }

        return new PimPolicy { MaximumDuration = max, RequiresJustification = justification, RequiresTicket = ticket, RequiresMfa = mfa, RequiresApproval = approval };
    }

    /// <summary>Self-activates one eligible role. The outcome's summary says whether it is active now or waiting for approval.</summary>
    public async Task<WriteOutcome> ActivateAsync(PimActivation request, CancellationToken ct)
    {
        var role = request.Role;
        var summary = $"Activate {role.DisplayName}";
        var hours = request.Duration.TotalHours;
        var requestText = $"POST roleAssignmentScheduleRequests selfActivate {role.RoleDefinitionId} for {XmlConvert.ToString(request.Duration)}";

        var body = new Dictionary<string, object?>
        {
            ["action"] = "selfActivate",
            ["principalId"] = _tenant.SignedInUserId,
            ["roleDefinitionId"] = role.RoleDefinitionId,
            ["directoryScopeId"] = "/",
            ["justification"] = string.IsNullOrWhiteSpace(request.Justification) ? null : request.Justification.Trim(),
            ["scheduleInfo"] = new Dictionary<string, object?>
            {
                ["startDateTime"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                ["expiration"] = new Dictionary<string, object?> { ["type"] = "afterDuration", ["duration"] = XmlConvert.ToString(request.Duration) },
            },
        };
        if (!string.IsNullOrWhiteSpace(request.TicketNumber))
        {
            body["ticketInfo"] = new Dictionary<string, object?> { ["ticketNumber"] = request.TicketNumber.Trim(), ["ticketSystem"] = request.TicketSystem?.Trim() };
        }

        string? status;
        try
        {
            using var doc = await _graph.SendAsync(HttpMethod.Post, "roleManagement/directory/roleAssignmentScheduleRequests", body, ct).ConfigureAwait(false);
            status = doc is null ? null : Text(doc.RootElement, "status");
        }
        catch (GraphException ex)
        {
            var friendly = ActivationError(ex);
            var id = Record(role, requestText, ActionOutcome.Failed, friendly, ex.RequestId);
            _logger.LogWarning("Activating {Role} failed: {Code}", role.RoleDefinitionId, ex.ErrorCode);
            return WriteOutcome.Failed(summary, ex, id, null) with { FriendlyError = friendly };
        }

        var pending = status is not null && status.Contains("Pending", StringComparison.OrdinalIgnoreCase);
        var message = pending
            ? $"{role.DisplayName} is waiting for approval"
            : $"{role.DisplayName} activated for {(hours == 1 ? "1 hour" : $"{hours:0.#} hours")}";
        var actionId = Record(role, requestText, ActionOutcome.Success, message, null);
        _logger.LogInformation("Requested activation of {Role}: {Status}", role.RoleDefinitionId, status);
        return new WriteOutcome { Success = true, Summary = message, ActionId = actionId };
    }

    private long Record(PimRole role, string request, ActionOutcome outcome, string message, string? requestId) => _actions.Record(new ActionRecord
    {
        Actor = _tenant.SignedInUpn,
        TenantId = _tenant.TenantId,
        TargetType = ObjectType.User,
        TargetId = _tenant.SignedInUserId,
        TargetName = _tenant.SignedInDisplayName,
        Action = "pim.activate",
        RequestSummary = request,
        Outcome = outcome,
        Message = message,
        GraphRequestId = requestId,
    });

    /// <summary>The read error as the admin should hear it. Licence first: it is the common case and the least obvious.</summary>
    internal string Unavailable(GraphException ex)
    {
        var text = $"{ex.ErrorCode} {ex.Message}";
        if (text.Contains("Premium", StringComparison.OrdinalIgnoreCase) || text.Contains("licen", StringComparison.OrdinalIgnoreCase)
            || text.Contains("P2", StringComparison.Ordinal) || (!_tenant.HasP2 && ex.StatusCode is 400 or 403))
        {
            return NoLicence;
        }

        if (ex.StatusCode == 403) return "Your sign-in was refused when reading your PIM roles. Grant the PIM permissions under Permissions, then try again.";
        return $"Your PIM roles could not be read: {GraphErrorText.Friendly(ex)}";
    }

    internal static string ActivationError(GraphException ex)
    {
        var code = ex.ErrorCode ?? string.Empty;
        if (code.Contains("AcrsValidationFailed", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("MFA", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("multi-factor", StringComparison.OrdinalIgnoreCase))
        {
            return "This role needs a fresh multi-factor sign-in to activate. Sign out, sign back in with MFA, then activate again.";
        }

        if (code.Contains("RoleAssignmentExists", StringComparison.OrdinalIgnoreCase)) return "This role is already active.";
        if (code.Contains("JustificationRule", StringComparison.OrdinalIgnoreCase)) return "This role's policy needs a justification.";
        if (code.Contains("TicketingRule", StringComparison.OrdinalIgnoreCase)) return "This role's policy needs a ticket number.";
        if (code.Contains("ExpirationRule", StringComparison.OrdinalIgnoreCase)) return "That duration is longer than this role's policy allows.";
        return GraphErrorText.Friendly(ex);
    }

    private static PimRole? Role(JsonElement item)
    {
        var id = Text(item, "roleDefinitionId");
        if (id is null) return null;
        var definition = item.TryGetProperty("roleDefinition", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        var template = definition.ValueKind == JsonValueKind.Object ? Text(definition, "templateId") ?? id : id;
        var name = definition.ValueKind == JsonValueKind.Object ? Text(definition, "displayName") : null;
        return new PimRole
        {
            RoleDefinitionId = id,
            RoleTemplateId = template,
            DisplayName = name ?? DirectoryRoleRef.From(null, template).DisplayName,
        };
    }

    private static IEnumerable<JsonElement> Values(JsonDocument? doc) =>
        doc is not null && doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var d) ? d : null;
}
