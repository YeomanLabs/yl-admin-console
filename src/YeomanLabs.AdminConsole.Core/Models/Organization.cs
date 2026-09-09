using System.Text.Json;
using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>Cache projection of the tenant <c>organization</c> object (docs/05-DATA-MODEL.md <c>organization</c>).</summary>
public sealed record Organization
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public bool OnPremisesSyncEnabled { get; init; }
    public DateTimeOffset? OnPremisesLastSyncDateTime { get; init; }
    public bool HasP1 { get; init; }
    public bool HasP2 { get; init; }
    public IReadOnlyList<string> VerifiedDomains { get; init; } = [];
    public required string Raw { get; init; }

    public static Organization FromJson(JsonElement e)
    {
        bool p1 = false, p2 = false;
        if (e.TryGetProperty("assignedPlans", out var plans) && plans.ValueKind == JsonValueKind.Array)
        {
            foreach (var plan in plans.EnumerateArray())
            {
                if (!string.Equals(plan.GetString("capabilityStatus"), "Enabled", StringComparison.OrdinalIgnoreCase)) continue;
                var id = plan.GetString("servicePlanId");
                if (string.Equals(id, TenantContext.AadPremiumP1PlanId, StringComparison.OrdinalIgnoreCase)) p1 = true;
                if (string.Equals(id, TenantContext.AadPremiumP2PlanId, StringComparison.OrdinalIgnoreCase)) p2 = true;
            }
        }

        var domains = new List<string>();
        if (e.TryGetProperty("verifiedDomains", out var vd) && vd.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in vd.EnumerateArray())
            {
                if (d.GetString("name") is { } name) domains.Add(name);
            }
        }

        return new Organization
        {
            Id = e.GetString("id") ?? throw new JsonException("organization without id"),
            DisplayName = e.GetString("displayName"),
            OnPremisesSyncEnabled = e.GetBool("onPremisesSyncEnabled") ?? false,
            OnPremisesLastSyncDateTime = e.GetDate("onPremisesLastSyncDateTime"),
            HasP1 = p1 || p2,
            HasP2 = p2,
            VerifiedDomains = domains,
            Raw = e.GetRawText(),
        };
    }
}
