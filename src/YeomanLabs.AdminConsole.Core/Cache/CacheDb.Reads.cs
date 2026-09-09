using System.Text.Json;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Cache;

/// <summary>
/// Typed reads for detail pages (doc 05 principle: detail views deserialize <c>raw</c>; nothing re-fetches from Graph just to
/// render cached data). Each returns null / empty when the object is not cached yet.
/// </summary>
public sealed partial class CacheDb
{
    public DirectoryUser? GetUser(string id) => Parse(GetRaw(CacheEntity.Users, id), DirectoryUser.FromJson);

    public DirectoryGroup? GetGroup(string id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw, member_count FROM groups WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ParseGroup(r.GetString(0), r.GetInt32(1));
    }

    public ManagedDevice? GetManagedDevice(string id) => Parse(GetRaw(CacheEntity.ManagedDevices, id), ManagedDevice.FromJson);

    public EntraDevice? GetEntraDevice(string id) => Parse(GetRaw(CacheEntity.Devices, id), EntraDevice.FromJson);

    /// <summary>Entra device object by <c>deviceId</c> (== Intune <c>azureADDeviceId</c>).</summary>
    public EntraDevice? GetEntraDeviceByDeviceId(string deviceId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw FROM devices WHERE device_id=$d;";
        cmd.Parameters.AddWithValue("$d", deviceId);
        return Parse(cmd.ExecuteScalar() as string, EntraDevice.FromJson);
    }

    /// <summary>Intune device whose Entra object id is <paramref name="deviceId"/>, for the Entra → Intune join.</summary>
    public ManagedDevice? GetManagedDeviceByAzureAdDeviceId(string deviceId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw FROM managed_devices WHERE azure_ad_device_id=$d LIMIT 1;";
        cmd.Parameters.AddWithValue("$d", deviceId);
        return Parse(cmd.ExecuteScalar() as string, ManagedDevice.FromJson);
    }

    /// <summary>Intune devices where the user is primary (doc 05 join <c>managed_devices.primary_user_id = users.id</c>).</summary>
    public IReadOnlyList<ManagedDevice> GetManagedDevicesForUser(string userId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw FROM managed_devices WHERE primary_user_id=$u ORDER BY device_name;";
        cmd.Parameters.AddWithValue("$u", userId);
        return ReadAll(cmd, ManagedDevice.FromJson);
    }

    /// <summary>Cached group memberships (doc 05 join <c>group_members.member_id = users.id</c>), with member counts.</summary>
    public IReadOnlyList<DirectoryGroup> GetGroupsForMember(string memberId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT g.raw, g.member_count FROM group_members gm JOIN groups g ON g.id = gm.group_id WHERE gm.member_id=$m ORDER BY g.display_name;";
        cmd.Parameters.AddWithValue("$m", memberId);
        using var r = cmd.ExecuteReader();
        var list = new List<DirectoryGroup>();
        while (r.Read()) list.Add(ParseGroup(r.GetString(0), r.GetInt32(1)));
        return list;
    }

    /// <summary>
    /// SKUs from the projected columns (doc 05: lists read the projection, detail views read <c>raw</c>). Building these
    /// from <c>raw</c> instead would make the list depend on Graph having returned every selected property.
    /// </summary>
    public IReadOnlyList<LicenseSku> GetSkus()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sku_id, part_number, friendly_name, enabled, consumed, raw FROM skus ORDER BY part_number;";
        using var r = cmd.ExecuteReader();
        var list = new List<LicenseSku>();
        while (r.Read())
        {
            var raw = r.GetString(5);
            string? capability = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                capability = doc.RootElement.GetString("capabilityStatus");
            }
            catch (JsonException)
            {
                // A malformed raw payload must not hide the SKU from the licence list.
            }

            list.Add(new LicenseSku
            {
                SkuId = r.GetString(0),
                SkuPartNumber = r.GetString(1),
                FriendlyName = r.IsDBNull(2) ? null : r.GetString(2),
                Enabled = r.GetInt32(3),
                Consumed = r.GetInt32(4),
                CapabilityStatus = capability,
                Raw = raw,
            });
        }

        return list;
    }

    public Organization? GetOrganization()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw FROM organization LIMIT 1;";
        return Parse(cmd.ExecuteScalar() as string, Organization.FromJson);
    }

    /// <summary>Cached members of a group as (id, type) pairs; the group page fetches names on demand from Graph.</summary>
    public IReadOnlyList<(string MemberId, string MemberType)> GetGroupMemberIds(string groupId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT member_id, member_type FROM group_members WHERE group_id=$g;";
        cmd.Parameters.AddWithValue("$g", groupId);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Users by id, for resolving cached members without a Graph call.</summary>
    public IReadOnlyList<DirectoryUser> GetUsers(IEnumerable<string> ids)
    {
        var list = new List<DirectoryUser>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw FROM users WHERE id=$id;";
        var p = cmd.Parameters.AddWithValue("$id", string.Empty);
        foreach (var id in ids)
        {
            p.Value = id;
            if (cmd.ExecuteScalar() is string raw) list.Add(DirectoryUser.FromJson(JsonDocument.Parse(raw).RootElement));
        }

        return list;
    }

    private static T? Parse<T>(string? raw, Func<JsonElement, T> parse) where T : class
    {
        if (raw is null) return null;
        using var doc = JsonDocument.Parse(raw);
        return parse(doc.RootElement);
    }

    private static DirectoryGroup ParseGroup(string raw, int memberCount)
    {
        using var doc = JsonDocument.Parse(raw);
        return DirectoryGroup.FromJson(doc.RootElement) with { MemberCount = memberCount };
    }

    private static List<T> ReadAll<T>(Microsoft.Data.Sqlite.SqliteCommand cmd, Func<JsonElement, T> parse)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read())
        {
            using var doc = JsonDocument.Parse(r.GetString(0));
            list.Add(parse(doc.RootElement));
        }

        return list;
    }
}
