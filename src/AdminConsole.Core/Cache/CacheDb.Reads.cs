using System.Text.Json;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Cache;

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
        cmd.CommandText = $"SELECT {GroupColumns} FROM groups WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadGroup(r) : null;
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
        cmd.CommandText = $"SELECT {GroupColumns.Replace("id,", "g.id,")} FROM group_members gm JOIN groups g ON g.id = gm.group_id WHERE gm.member_id=$m ORDER BY g.display_name;";
        cmd.Parameters.AddWithValue("$m", memberId);
        using var r = cmd.ExecuteReader();
        var list = new List<DirectoryGroup>();
        while (r.Read()) list.Add(ReadGroup(r));
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

    private const string GroupColumns = "id, display_name, description, mail, kind, dynamic, synced, role_assignable, member_count, raw";

    /// <summary>
    /// Groups from the projected columns (doc 05), not by re-parsing <c>raw</c>: the columns are what the sync wrote and
    /// what the FTS index and member counts agree with, and they are present even if a payload was ever partial.
    /// </summary>
    private static DirectoryGroup ReadGroup(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        DisplayName = r.IsDBNull(1) ? null : r.GetString(1),
        Description = r.IsDBNull(2) ? null : r.GetString(2),
        Mail = r.IsDBNull(3) ? null : r.GetString(3),
        Kind = Enum.TryParse<GroupKind>(r.GetString(4), out var kind) ? kind : GroupKind.Security,
        IsDynamic = r.GetInt64(5) != 0,
        OnPremisesSyncEnabled = r.GetInt64(6) != 0,
        IsAssignableToRole = r.GetInt64(7) != 0,
        MemberCount = r.GetInt32(8),
        Raw = r.GetString(9),
    };

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
