using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>Cache projection of an Entra group (docs/05-DATA-MODEL.md <c>groups</c>).</summary>
public sealed record DirectoryGroup
{
    public const string DynamicReason = "Dynamic membership — members are computed from the rule";
    public const string SyncedReason = "Mastered in on-prem AD (Phase 2)";
    public const string RoleAssignableReason = "Role-assignable group — needs Privileged Role Administrator";

    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Mail { get; init; }
    public required GroupKind Kind { get; init; }
    public required bool IsDynamic { get; init; }
    public required bool OnPremisesSyncEnabled { get; init; }
    public required bool IsAssignableToRole { get; init; }
    public int MemberCount { get; init; }
    public required string Raw { get; init; }

    public bool IsM365 => Kind == GroupKind.M365;
    public bool IsSynced => OnPremisesSyncEnabled;

    /// <summary>Palette/list badges in display order.</summary>
    public IReadOnlyList<string> Badges
    {
        get
        {
            var list = new List<string>(4);
            list.Add(Kind switch { GroupKind.M365 => "M365", GroupKind.Security => "Security", _ => "Distribution" });
            if (IsDynamic) list.Add("Dynamic");
            if (IsSynced) list.Add("Synced");
            if (IsAssignableToRole) list.Add("Role-assignable");
            return list;
        }
    }

    /// <summary>
    /// The doc 04 skip rules for manual membership changes. Null means adds/removes are allowed (subject to scopes);
    /// role-assignable groups are allowed only when <paramref name="hasPrivilegedRoleAdmin"/>.
    /// </summary>
    public string? MembershipChangeBlockedReason(bool hasPrivilegedRoleAdmin = false)
    {
        if (IsDynamic) return DynamicReason;
        if (OnPremisesSyncEnabled) return SyncedReason;
        if (IsAssignableToRole && !hasPrivilegedRoleAdmin) return RoleAssignableReason;
        return null;
    }

    public static DirectoryGroup FromJson(JsonElement e)
    {
        var groupTypes = e.GetStrings("groupTypes");
        var mailEnabled = e.GetBool("mailEnabled") ?? false;
        var securityEnabled = e.GetBool("securityEnabled") ?? false;
        return new DirectoryGroup
        {
            Id = e.GetString("id") ?? throw new JsonException("group without id"),
            DisplayName = e.GetString("displayName"),
            Description = e.GetString("description"),
            Mail = e.GetString("mail"),
            Kind = DeriveKind(groupTypes, mailEnabled, securityEnabled),
            IsDynamic = groupTypes.Contains("DynamicMembership", StringComparer.OrdinalIgnoreCase),
            OnPremisesSyncEnabled = e.GetBool("onPremisesSyncEnabled") ?? false,
            IsAssignableToRole = e.GetBool("isAssignableToRole") ?? false,
            Raw = e.GetRawText(),
        };
    }

    /// <summary>Doc 05: <c>Unified</c> → M365; else security-only → Security; else distribution / mail-enabled security.</summary>
    public static GroupKind DeriveKind(IReadOnlyList<string> groupTypes, bool mailEnabled, bool securityEnabled)
    {
        if (groupTypes.Contains("Unified", StringComparer.OrdinalIgnoreCase)) return GroupKind.M365;
        if (securityEnabled && !mailEnabled) return GroupKind.Security;
        return GroupKind.DistributionOrMailSecurity;
    }

    public JsonDocument ParseRaw() => JsonDocument.Parse(Raw);
}
