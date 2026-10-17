using System.Text;
using Microsoft.Data.Sqlite;
using AdminConsole.Core.Cache.Browse;

namespace AdminConsole.Core.Cache;

/// <summary>
/// M9.1 — reads that back the Users, Groups and Devices list pages: a page of rows plus the total the query matched.
/// </summary>
/// <remarks>
/// These are deliberately separate from <see cref="SearchService"/>. Search answers "I know who I want, find them" and
/// ranks by relevance over an FTS index; a list answers "show me everyone and let me look", so it sorts by a column
/// the admin picked and narrows with a substring rather than a token. FTS cannot match the middle of a word, which is
/// what a filter box has to do while someone is still typing.
/// </remarks>
public sealed partial class CacheDb
{
    /// <summary>The Entra and Intune device tables as one set. A device in both appears once, from Intune, because
    /// that row is the one with a serial, a compliance state and something to act on.</summary>
    private const string DeviceUnion = """
        SELECT 'intune' AS source, md.id AS id, md.device_name AS device_name, md.serial AS serial,
               md.model AS model, md.manufacturer AS manufacturer, md.os AS os, md.os_version AS os_version,
               md.compliance AS compliance, md.encrypted AS encrypted, md.primary_user_id AS primary_user_id,
               md.primary_user_name AS primary_user_name, md.primary_user_upn AS primary_user_upn,
               md.last_sync AS last_activity, md.enrolled AS enrolled, md.autopilot AS autopilot,
               md.owner_type AS owner_type, d.trust_type AS trust_type
        FROM managed_devices md
        LEFT JOIN devices d ON d.device_id = md.azure_ad_device_id
        UNION ALL
        SELECT 'entra' AS source, d.id AS id, d.display_name AS device_name, NULL AS serial,
               NULL AS model, NULL AS manufacturer, d.os AS os, d.os_version AS os_version,
               NULL AS compliance, NULL AS encrypted, NULL AS primary_user_id,
               NULL AS primary_user_name, NULL AS primary_user_upn,
               d.last_signin AS last_activity, NULL AS enrolled, 0 AS autopilot,
               NULL AS owner_type, d.trust_type AS trust_type
        FROM devices d
        WHERE NOT EXISTS (SELECT 1 FROM managed_devices m2 WHERE m2.azure_ad_device_id = d.device_id)
        """;

    public BrowsePage<UserListRow> BrowseUsers(BrowseQuery query) => Browse(
        "users",
        "id, upn, display_name, mail, job_title, department, account_enabled, licensed, synced, user_type, created",
        UserBrowse.Filters, UserBrowse.Sorts, UserBrowse.DefaultSort, UserBrowse.TextColumns, UserBrowse.Fields, query,
        r => new UserListRow
        {
            Id = r.GetString(0),
            Upn = r.GetString(1),
            DisplayName = r.IsDBNull(2) ? null : r.GetString(2),
            Mail = r.IsDBNull(3) ? null : r.GetString(3),
            JobTitle = r.IsDBNull(4) ? null : r.GetString(4),
            Department = r.IsDBNull(5) ? null : r.GetString(5),
            Enabled = r.GetInt64(6) != 0,
            Licensed = r.GetInt64(7) != 0,
            Synced = r.GetInt64(8) != 0,
            UserType = r.IsDBNull(9) ? null : r.GetString(9),
            Created = ReadDate(r, 10),
        });

    public BrowsePage<GroupListRow> BrowseGroups(BrowseQuery query) => Browse(
        "groups",
        "id, display_name, description, mail, kind, dynamic, synced, role_assignable, member_count",
        GroupBrowse.Filters, GroupBrowse.Sorts, GroupBrowse.DefaultSort, GroupBrowse.TextColumns, GroupBrowse.Fields, query,
        r => new GroupListRow
        {
            Id = r.GetString(0),
            DisplayName = r.IsDBNull(1) ? null : r.GetString(1),
            Description = r.IsDBNull(2) ? null : r.GetString(2),
            Mail = r.IsDBNull(3) ? null : r.GetString(3),
            Kind = r.GetString(4),
            Dynamic = r.GetInt64(5) != 0,
            Synced = r.GetInt64(6) != 0,
            RoleAssignable = r.GetInt64(7) != 0,
            MemberCount = (int)r.GetInt64(8),
        });

    public BrowsePage<DeviceListRow> BrowseDevices(BrowseQuery query) => Browse(
        $"({DeviceUnion})",
        "id, source, device_name, serial, model, manufacturer, os, os_version, compliance, encrypted, " +
        "primary_user_id, primary_user_name, primary_user_upn, last_activity, enrolled, autopilot, owner_type, trust_type",
        DeviceBrowse.Filters, DeviceBrowse.Sorts, DeviceBrowse.DefaultSort, DeviceBrowse.TextColumns, DeviceBrowse.Fields, query,
        r => new DeviceListRow
        {
            Id = r.GetString(0),
            Source = r.GetString(1) == "intune" ? DeviceSource.Intune : DeviceSource.Entra,
            DeviceName = r.IsDBNull(2) ? null : r.GetString(2),
            Serial = r.IsDBNull(3) ? null : r.GetString(3),
            Model = r.IsDBNull(4) ? null : r.GetString(4),
            Manufacturer = r.IsDBNull(5) ? null : r.GetString(5),
            Os = r.IsDBNull(6) ? null : r.GetString(6),
            OsVersion = r.IsDBNull(7) ? null : r.GetString(7),
            Compliance = r.IsDBNull(8) ? null : r.GetString(8),
            Encrypted = r.IsDBNull(9) ? null : r.GetInt64(9) != 0,
            PrimaryUserId = r.IsDBNull(10) ? null : r.GetString(10),
            PrimaryUserName = r.IsDBNull(11) ? null : r.GetString(11),
            PrimaryUserUpn = r.IsDBNull(12) ? null : r.GetString(12),
            LastActivity = ReadDate(r, 13),
            Enrolled = ReadDate(r, 14),
            Autopilot = !r.IsDBNull(15) && r.GetInt64(15) != 0,
            OwnerType = r.IsDBNull(16) ? null : r.GetString(16),
            TrustType = r.IsDBNull(17) ? null : r.GetString(17),
        });

    /// <summary>How many rows each filter would leave, so the chips can show a count instead of the admin ticking one
    /// to find out it matches nothing. Counted against the other groups' filters, the way a faceted list should.</summary>
    public IReadOnlyDictionary<string, int> CountUserFilters(BrowseQuery query) =>
        CountFilters("users", UserBrowse.Filters, UserBrowse.TextColumns, UserBrowse.Fields, query);

    public IReadOnlyDictionary<string, int> CountGroupFilters(BrowseQuery query) =>
        CountFilters("groups", GroupBrowse.Filters, GroupBrowse.TextColumns, GroupBrowse.Fields, query);

    public IReadOnlyDictionary<string, int> CountDeviceFilters(BrowseQuery query) =>
        CountFilters($"({DeviceUnion})", DeviceBrowse.Filters, DeviceBrowse.TextColumns, DeviceBrowse.Fields, query);

    // ---- the shared machinery ---------------------------------------------------------------------------------

    private BrowsePage<T> Browse<T>(
        string from,
        string columns,
        IReadOnlyList<BrowseFilter> catalog,
        IReadOnlyList<BrowseSort> sorts,
        BrowseSort defaultSort,
        IReadOnlyList<string> textColumns,
        IReadOnlyList<BrowseField> fields,
        BrowseQuery query,
        Func<SqliteDataReader, T> read)
    {
        var (where, parameters, problems) = BuildWhere(catalog, textColumns, fields, query);
        var sort = sorts.FirstOrDefault(s => s.Key == query.SortKey) ?? defaultSort;
        var take = Math.Clamp(query.Take, 1, 5000);
        var skip = Math.Max(0, query.Skip);

        using var conn = OpenConnection();

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM {from} {where};";
        Bind(countCmd, parameters);
        var total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);

        using var cmd = conn.CreateCommand();
        // A NULL name sorts last in both directions: a row with no display name is the least useful thing to put at
        // the top of a list, whichever way the admin clicked the header.
        cmd.CommandText = $"""
            SELECT {columns} FROM {from} {where}
            ORDER BY ({sort.Sql} IS NULL OR {sort.Sql} = ''), {sort.Sql} {(query.Descending ? "DESC" : "ASC")}
            LIMIT $take OFFSET $skip;
            """;
        Bind(cmd, parameters);
        cmd.Parameters.AddWithValue("$take", take);
        cmd.Parameters.AddWithValue("$skip", skip);

        var rows = new List<T>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(read(reader));

        return new BrowsePage<T>(rows, total, skip) { Problems = problems };
    }

    private IReadOnlyDictionary<string, int> CountFilters(
        string from, IReadOnlyList<BrowseFilter> catalog, IReadOnlyList<string> textColumns, IReadOnlyList<BrowseField> fields, BrowseQuery query)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var conn = OpenConnection();

        foreach (var filter in catalog)
        {
            // Every group except this filter's own, so ticking a second chip in the same group adds rather than
            // subtracts — the count says what would happen, not what a different filter model would do.
            var others = query with { FilterKeys = [.. query.FilterKeys.Where(k => Group(catalog, k) != filter.Group)] };
            var (where, parameters, _) = BuildWhere(catalog, textColumns, fields, others);
            var glue = where.Length == 0 ? "WHERE" : "AND";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {from} {where} {glue} ({filter.Sql});";
            Bind(cmd, parameters);
            counts[filter.Key] = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        return counts;
    }

    private static string? Group(IReadOnlyList<BrowseFilter> catalog, string key) =>
        catalog.FirstOrDefault(f => f.Key == key)?.Group;

    /// <summary>
    /// Builds the WHERE clause: filters OR-ed within a group and AND-ed across groups, then the free text AND-ed on
    /// top. Filter SQL comes only from the catalog; the text is always a parameter.
    /// </summary>
    private static (string Sql, List<(string Name, object Value)> Parameters, IReadOnlyList<string> Problems) BuildWhere(
        IReadOnlyList<BrowseFilter> catalog, IReadOnlyList<string> textColumns, IReadOnlyList<BrowseField> fields, BrowseQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<(string, object)>();

        var selected = query.FilterKeys
            .Select(k => catalog.FirstOrDefault(f => f.Key == k))
            .Where(f => f is not null)
            .Select(f => f!)
            .GroupBy(f => f.Group, StringComparer.Ordinal);

        foreach (var group in selected)
        {
            var ors = group.Select(f => f.Sql).ToList();
            clauses.Add(ors.Count == 1 ? ors[0] : "(" + string.Join(" OR ", ors) + ")");
        }

        // M18.4: key=value parts become fixed fragments with bound values; what is left is the free text.
        var parsed = FieldQuery.Parse(query.Text, fields);
        for (var i = 0; i < parsed.Conditions.Count; i++)
        {
            var (sql, value, whole) = parsed.Conditions[i];
            if (value is null)
            {
                clauses.Add(sql);
                continue;
            }

            var name = $"$f{i}";
            parameters.Add((name, whole ? Escape(value) : "%" + Escape(value) + "%"));
            clauses.Add("(" + sql.Replace("@v", name, StringComparison.Ordinal) + ")");
        }

        var text = parsed.FreeText.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            // Substring, not prefix: an admin narrowing a device list types the middle of an asset tag as often as
            // the start of it. ESCAPE keeps a literal % or _ in the box from matching everything.
            parameters.Add(("$text", "%" + Escape(text) + "%"));
            var ors = textColumns.Select(c => $"{c} LIKE $text ESCAPE '\\'");
            clauses.Add("(" + string.Join(" OR ", ors) + ")");
        }

        return (clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses), parameters, parsed.Problems);
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    private static void Bind(SqliteCommand cmd, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
    }
}
