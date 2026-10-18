using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Licensing;

namespace AdminConsole.Core.Reports;

/// <summary>
/// M19.2 — seats from <c>subscribedSkus</c> (the figures the admin centre's Licences page shows), and one walk of the
/// users for how each licence is held (<c>licenseAssignmentStates</c>: a null <c>assignedByGroup</c> is direct) and
/// when each account last signed in.
/// </summary>
public sealed class LicenceReportService
{
    public const string Kind = "licences";

    private const string UserSelect = "id,displayName,userPrincipalName,accountEnabled,createdDateTime,assignedLicenses,licenseAssignmentStates";

    private readonly GraphClient _graph;
    private readonly ILogger<LicenceReportService> _logger;

    public LicenceReportService(GraphClient graph, ILogger<LicenceReportService> logger)
    {
        _graph = graph;
        _logger = logger;
    }

    public async Task<LicenceReport> BuildAsync(CancellationToken ct, int quietDays = 90)
    {
        var skus = new List<(string Id, string Part, int Enabled, int Warning, int Suspended, int Consumed)>();
        await foreach (var s in _graph.GetAllPagesAsync("subscribedSkus?$select=skuId,skuPartNumber,prepaidUnits,consumedUnits,capabilityStatus", ct).ConfigureAwait(false))
        {
            var units = s.TryGetProperty("prepaidUnits", out var u) ? u : default;
            skus.Add((Text(s, "skuId") ?? string.Empty, Text(s, "skuPartNumber") ?? string.Empty,
                Int(units, "enabled"), Int(units, "warning"), Int(units, "suspended"), Int(s, "consumedUnits")));
        }

        // signInActivity needs P1 and AuditLog.Read.All; without them the whole request fails, so ask again without it.
        string? quietUnavailable = null;
        var users = new List<JsonElement>();
        try
        {
            await foreach (var u in _graph.GetAllPagesAsync($"users?$select={UserSelect},signInActivity&$top=500", ct).ConfigureAwait(false)) users.Add(u.Clone());
        }
        catch (GraphException ex) when (ex.StatusCode is 400 or 403)
        {
            _logger.LogInformation("Users with signInActivity refused ({Status} {Code}); reading without it", ex.StatusCode, ex.ErrorCode);
            quietUnavailable = "Sign-in activity needs Entra ID P1 and the audit-log permission, so quiet accounts cannot be judged here.";
            users.Clear();
            await foreach (var u in _graph.GetAllPagesAsync($"users?$select={UserSelect}&$top=999", ct).ConfigureAwait(false)) users.Add(u.Clone());
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-quietDays);
        var direct = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var group = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var disabled = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var quiet = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = skus.ToDictionary(s => s.Id, s => SkuNames.For(s.Part) ?? s.Part, StringComparer.OrdinalIgnoreCase);
        var holders = new List<LicensedUserRow>();

        foreach (var user in users)
        {
            var held = Held(user);
            if (held.Count == 0) continue;

            var enabled = !user.TryGetProperty("accountEnabled", out var ae) || ae.ValueKind != JsonValueKind.False;
            var created = Date(user, "createdDateTime");
            var last = LastSignIn(user);
            var isQuiet = quietUnavailable is null && enabled && (last ?? created ?? now) < cutoff;

            foreach (var (sku, by) in held)
            {
                if (by.Direct) direct[sku] = direct.GetValueOrDefault(sku) + 1;
                if (by.Group) group[sku] = group.GetValueOrDefault(sku) + 1;
                if (!enabled) disabled[sku] = disabled.GetValueOrDefault(sku) + 1;
                if (isQuiet) quiet[sku] = quiet.GetValueOrDefault(sku) + 1;
            }

            {
                holders.Add(new LicensedUserRow
                {
                    UserId = Text(user, "id") ?? string.Empty,
                    DisplayName = Text(user, "displayName") ?? Text(user, "userPrincipalName") ?? string.Empty,
                    Upn = Text(user, "userPrincipalName") ?? string.Empty,
                    Enabled = enabled,
                    Licences = [.. held.Select(h => names.GetValueOrDefault(h.Sku, h.Sku))],
                    SkuIds = [.. held.Select(h => h.Sku)],
                    AssignedBy = [.. held.Select(h => h.By.Direct && h.By.Group ? "Direct and group" : h.By.Group ? "Group" : "Direct")],
                    LastSignIn = last,
                    Created = created,
                    IsQuiet = isQuiet,
                });
            }
        }

        var flagged = holders.Where(h => h.IsFlagged).ToList();
        _logger.LogInformation("Licence report: {Skus} subscriptions, {Users} users read, {Holders} licensed, {Flagged} flagged", skus.Count, users.Count, holders.Count, flagged.Count);
        return new LicenceReport
        {
            TakenAt = DateTimeOffset.Now,
            QuietDays = quietDays,
            QuietUnavailable = quietUnavailable,
            Skus = [.. skus.Select(s => new LicenceSkuRow
            {
                SkuId = s.Id,
                PartNumber = s.Part,
                Name = names[s.Id],
                Enabled = s.Enabled,
                Warning = s.Warning,
                Suspended = s.Suspended,
                Consumed = s.Consumed,
                Direct = direct.GetValueOrDefault(s.Id),
                ViaGroup = group.GetValueOrDefault(s.Id),
                Disabled = disabled.GetValueOrDefault(s.Id),
                Quiet = quiet.GetValueOrDefault(s.Id),
            }).OrderBy(s => s.LooksFree).ThenBy(s => s.IsSuspended).ThenByDescending(s => s.Consumed).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)],
            Flagged = [.. flagged.OrderBy(f => f.Enabled).ThenBy(f => f.LastSignIn ?? DateTimeOffset.MinValue).ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
            Holders = [.. holders.OrderBy(h => h.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
        };
    }

    /// <summary>Each licence the user holds and how: direct, through a group, or both.</summary>
    internal static IReadOnlyList<(string Sku, (bool Direct, bool Group) By)> Held(JsonElement user)
    {
        var by = new Dictionary<string, (bool Direct, bool Group)>(StringComparer.OrdinalIgnoreCase);
        if (user.TryGetProperty("licenseAssignmentStates", out var states) && states.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in states.EnumerateArray())
            {
                if (Text(s, "skuId") is not { } sku) continue;
                // A failed group assignment ("Error") holds no seat; it is the group's problem to report, not a holding.
                if (string.Equals(Text(s, "state"), "Error", StringComparison.OrdinalIgnoreCase)) continue;
                var viaGroup = Text(s, "assignedByGroup") is not null;
                var known = by.GetValueOrDefault(sku);
                by[sku] = viaGroup ? (known.Direct, true) : (true, known.Group);
            }
        }
        else if (user.TryGetProperty("assignedLicenses", out var assigned) && assigned.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assigned.EnumerateArray())
            {
                if (Text(a, "skuId") is { } sku) by[sku] = (true, false);
            }
        }

        return [.. by.Select(kv => (kv.Key, kv.Value))];
    }

    internal static DateTimeOffset? LastSignIn(JsonElement user)
    {
        if (!user.TryGetProperty("signInActivity", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        return new[] { Date(a, "lastSignInDateTime"), Date(a, "lastNonInteractiveSignInDateTime"), Date(a, "lastSuccessfulSignInDateTime") }
            .Where(d => d is not null).Max();
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var d) ? d : null;
}
