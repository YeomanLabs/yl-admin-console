using Microsoft.Data.Sqlite;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Cache;

/// <summary>
/// Typed upserts/deletes bound to one write transaction (obtain via <see cref="CacheDb.Write(Action{CacheWriter})"/>).
/// Upserts use <c>ON CONFLICT DO UPDATE</c> so the rowid — and therefore the FTS5 external-content index — stays stable.
/// Commands are prepared once per writer so a 500-row batch is one prepare + 500 executes.
/// </summary>
public sealed class CacheWriter : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteTransaction _tx;
    private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);
    private readonly string _now = CacheDb.FormatDate(DateTimeOffset.UtcNow);

    internal CacheWriter(SqliteConnection conn, SqliteTransaction tx)
    {
        _conn = conn;
        _tx = tx;
    }

    public SqliteConnection Connection => _conn;
    public SqliteTransaction Transaction => _tx;

    // ---- users -------------------------------------------------------------------------------------------------

    public void UpsertUser(DirectoryUser u)
    {
        var cmd = Command("user-upsert", """
            INSERT INTO users (id, upn, display_name, mail, given_name, surname, job_title, department, employee_id, phones_digits,
                               account_enabled, user_type, synced, licensed, created, raw, updated_at)
            VALUES ($id, $upn, $display_name, $mail, $given_name, $surname, $job_title, $department, $employee_id, $phones_digits,
                    $account_enabled, $user_type, $synced, $licensed, $created, $raw, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                upn=excluded.upn, display_name=excluded.display_name, mail=excluded.mail, given_name=excluded.given_name,
                surname=excluded.surname, job_title=excluded.job_title, department=excluded.department, employee_id=excluded.employee_id,
                phones_digits=excluded.phones_digits, account_enabled=excluded.account_enabled, user_type=excluded.user_type,
                synced=excluded.synced, licensed=excluded.licensed, created=excluded.created, raw=excluded.raw, updated_at=excluded.updated_at;
            """,
            "$id", "$upn", "$display_name", "$mail", "$given_name", "$surname", "$job_title", "$department", "$employee_id", "$phones_digits",
            "$account_enabled", "$user_type", "$synced", "$licensed", "$created", "$raw", "$updated_at");
        Set(cmd, "$id", u.Id);
        Set(cmd, "$upn", u.UserPrincipalName);
        Set(cmd, "$display_name", u.DisplayName);
        Set(cmd, "$mail", u.Mail);
        Set(cmd, "$given_name", u.GivenName);
        Set(cmd, "$surname", u.Surname);
        Set(cmd, "$job_title", u.JobTitle);
        Set(cmd, "$department", u.Department);
        Set(cmd, "$employee_id", u.EmployeeId);
        Set(cmd, "$phones_digits", u.PhonesDigits);
        Set(cmd, "$account_enabled", u.AccountEnabled);
        Set(cmd, "$user_type", u.UserType);
        Set(cmd, "$synced", u.OnPremisesSyncEnabled);
        Set(cmd, "$licensed", u.Licensed);
        Set(cmd, "$created", u.CreatedDateTime);
        Set(cmd, "$raw", u.Raw);
        Set(cmd, "$updated_at", _now);
        cmd.ExecuteNonQuery();
    }

    // ---- groups ------------------------------------------------------------------------------------------------

    public void UpsertGroup(DirectoryGroup g)
    {
        var cmd = Command("group-upsert", """
            INSERT INTO groups (id, display_name, description, mail, kind, dynamic, synced, role_assignable, member_count, raw, updated_at)
            VALUES ($id, $display_name, $description, $mail, $kind, $dynamic, $synced, $role_assignable,
                    COALESCE((SELECT member_count FROM groups WHERE id=$id), $member_count), $raw, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                display_name=excluded.display_name, description=excluded.description, mail=excluded.mail, kind=excluded.kind,
                dynamic=excluded.dynamic, synced=excluded.synced, role_assignable=excluded.role_assignable,
                raw=excluded.raw, updated_at=excluded.updated_at;
            """,
            "$id", "$display_name", "$description", "$mail", "$kind", "$dynamic", "$synced", "$role_assignable", "$member_count", "$raw", "$updated_at");
        Set(cmd, "$id", g.Id);
        Set(cmd, "$display_name", g.DisplayName);
        Set(cmd, "$description", g.Description);
        Set(cmd, "$mail", g.Mail);
        Set(cmd, "$kind", g.Kind.ToString());
        Set(cmd, "$dynamic", g.IsDynamic);
        Set(cmd, "$synced", g.OnPremisesSyncEnabled);
        Set(cmd, "$role_assignable", g.IsAssignableToRole);
        Set(cmd, "$member_count", g.MemberCount);
        Set(cmd, "$raw", g.Raw);
        Set(cmd, "$updated_at", _now);
        cmd.ExecuteNonQuery();
    }

    public void AddGroupMember(string groupId, string memberId, string memberType)
    {
        var cmd = Command("gm-add",
            "INSERT INTO group_members (group_id, member_id, member_type) VALUES ($g, $m, $t) ON CONFLICT(group_id, member_id) DO UPDATE SET member_type=excluded.member_type;",
            "$g", "$m", "$t");
        Set(cmd, "$g", groupId);
        Set(cmd, "$m", memberId);
        Set(cmd, "$t", memberType);
        cmd.ExecuteNonQuery();
    }

    public void RemoveGroupMember(string groupId, string memberId)
    {
        var cmd = Command("gm-remove", "DELETE FROM group_members WHERE group_id=$g AND member_id=$m;", "$g", "$m");
        Set(cmd, "$g", groupId);
        Set(cmd, "$m", memberId);
        cmd.ExecuteNonQuery();
    }

    public void ClearGroupMembers(string groupId)
    {
        var cmd = Command("gm-clear", "DELETE FROM group_members WHERE group_id=$g;", "$g");
        Set(cmd, "$g", groupId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Recomputes <c>groups.member_count</c> from <c>group_members</c> for one group (or all when null).</summary>
    public void RefreshMemberCount(string? groupId = null)
    {
        if (groupId is null)
        {
            Command("gm-count-all", "UPDATE groups SET member_count = (SELECT COUNT(*) FROM group_members gm WHERE gm.group_id = groups.id);").ExecuteNonQuery();
            return;
        }

        var cmd = Command("gm-count-one", "UPDATE groups SET member_count = (SELECT COUNT(*) FROM group_members WHERE group_id=$g) WHERE id=$g;", "$g");
        Set(cmd, "$g", groupId);
        cmd.ExecuteNonQuery();
    }

    // ---- Entra devices -----------------------------------------------------------------------------------------

    public void UpsertDevice(EntraDevice d)
    {
        var cmd = Command("device-upsert", """
            INSERT INTO devices (id, device_id, display_name, os, os_version, trust_type, compliant, managed, enabled, last_signin, raw, updated_at)
            VALUES ($id, $device_id, $display_name, $os, $os_version, $trust_type, $compliant, $managed, $enabled, $last_signin, $raw, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                device_id=excluded.device_id, display_name=excluded.display_name, os=excluded.os, os_version=excluded.os_version,
                trust_type=excluded.trust_type, compliant=excluded.compliant, managed=excluded.managed, enabled=excluded.enabled,
                last_signin=excluded.last_signin, raw=excluded.raw, updated_at=excluded.updated_at;
            """,
            "$id", "$device_id", "$display_name", "$os", "$os_version", "$trust_type", "$compliant", "$managed", "$enabled", "$last_signin", "$raw", "$updated_at");
        Set(cmd, "$id", d.Id);
        Set(cmd, "$device_id", d.DeviceId);
        Set(cmd, "$display_name", d.DisplayName);
        Set(cmd, "$os", d.OperatingSystem);
        Set(cmd, "$os_version", d.OperatingSystemVersion);
        Set(cmd, "$trust_type", d.TrustType);
        Set(cmd, "$compliant", d.IsCompliant);
        Set(cmd, "$managed", d.IsManaged);
        Set(cmd, "$enabled", d.AccountEnabled);
        Set(cmd, "$last_signin", d.ApproximateLastSignInDateTime);
        Set(cmd, "$raw", d.Raw);
        Set(cmd, "$updated_at", _now);
        cmd.ExecuteNonQuery();
    }

    // ---- Intune managed devices --------------------------------------------------------------------------------

    public void UpsertManagedDevice(ManagedDevice m)
    {
        var cmd = Command("md-upsert", """
            INSERT INTO managed_devices (id, azure_ad_device_id, device_name, serial, model, manufacturer, os, os_version, compliance, encrypted,
                                         primary_user_id, primary_user_upn, primary_user_name, last_sync, enrolled, autopilot, owner_type, raw, updated_at)
            VALUES ($id, $aad, $device_name, $serial, $model, $manufacturer, $os, $os_version, $compliance, $encrypted,
                    $pu_id, $pu_upn, $pu_name, $last_sync, $enrolled, $autopilot, $owner_type, $raw, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                azure_ad_device_id=excluded.azure_ad_device_id, device_name=excluded.device_name, serial=excluded.serial, model=excluded.model,
                manufacturer=excluded.manufacturer, os=excluded.os, os_version=excluded.os_version, compliance=excluded.compliance,
                encrypted=excluded.encrypted, primary_user_id=excluded.primary_user_id, primary_user_upn=excluded.primary_user_upn,
                primary_user_name=excluded.primary_user_name, last_sync=excluded.last_sync, enrolled=excluded.enrolled,
                autopilot=excluded.autopilot, owner_type=excluded.owner_type, raw=excluded.raw, updated_at=excluded.updated_at;
            """,
            "$id", "$aad", "$device_name", "$serial", "$model", "$manufacturer", "$os", "$os_version", "$compliance", "$encrypted",
            "$pu_id", "$pu_upn", "$pu_name", "$last_sync", "$enrolled", "$autopilot", "$owner_type", "$raw", "$updated_at");
        Set(cmd, "$id", m.Id);
        Set(cmd, "$aad", m.AzureAdDeviceId);
        Set(cmd, "$device_name", m.DeviceName);
        Set(cmd, "$serial", m.SerialNumber);
        Set(cmd, "$model", m.Model);
        Set(cmd, "$manufacturer", m.Manufacturer);
        Set(cmd, "$os", m.OperatingSystem);
        Set(cmd, "$os_version", m.OsVersion);
        Set(cmd, "$compliance", m.ComplianceState);
        Set(cmd, "$encrypted", m.IsEncrypted);
        Set(cmd, "$pu_id", m.PrimaryUserId);
        Set(cmd, "$pu_upn", m.PrimaryUserUpn);
        Set(cmd, "$pu_name", m.PrimaryUserDisplayName);
        Set(cmd, "$last_sync", m.LastSyncDateTime);
        Set(cmd, "$enrolled", m.EnrolledDateTime);
        Set(cmd, "$autopilot", m.AutopilotEnrolled);
        Set(cmd, "$owner_type", m.OwnerType);
        Set(cmd, "$raw", m.Raw);
        Set(cmd, "$updated_at", _now);
        cmd.ExecuteNonQuery();
    }

    // ---- Exchange mailboxes ------------------------------------------------------------------------------------

    public void UpsertMailbox(AdminConsole.Core.Exchange.MailboxSummary m)
    {
        var cmd = Command("mailbox-upsert", """
            INSERT INTO mailboxes (user_id, recipient_type, primary_smtp, hidden, dir_synced, updated_at)
            VALUES ($user_id, $recipient_type, $primary_smtp, $hidden, $dir_synced, $updated_at)
            ON CONFLICT(user_id) DO UPDATE SET
                recipient_type=excluded.recipient_type, primary_smtp=excluded.primary_smtp, hidden=excluded.hidden,
                dir_synced=excluded.dir_synced, updated_at=excluded.updated_at;
            """,
            "$user_id", "$recipient_type", "$primary_smtp", "$hidden", "$dir_synced", "$updated_at");
        Set(cmd, "$user_id", m.UserId);
        Set(cmd, "$recipient_type", m.RecipientType);
        Set(cmd, "$primary_smtp", m.PrimarySmtp);
        Set(cmd, "$hidden", m.HiddenFromAddressLists);
        Set(cmd, "$dir_synced", m.DirSynced);
        Set(cmd, "$updated_at", _now);
        cmd.ExecuteNonQuery();
    }

    // ---- SKUs / organization -----------------------------------------------------------------------------------

    public void UpsertSku(LicenseSku s)
    {
        var cmd = Command("sku-upsert", """
            INSERT INTO skus (sku_id, part_number, friendly_name, enabled, consumed, raw)
            VALUES ($sku_id, $part_number, $friendly_name, $enabled, $consumed, $raw)
            ON CONFLICT(sku_id) DO UPDATE SET
                part_number=excluded.part_number, friendly_name=excluded.friendly_name, enabled=excluded.enabled, consumed=excluded.consumed, raw=excluded.raw;
            """,
            "$sku_id", "$part_number", "$friendly_name", "$enabled", "$consumed", "$raw");
        Set(cmd, "$sku_id", s.SkuId);
        Set(cmd, "$part_number", s.SkuPartNumber);
        Set(cmd, "$friendly_name", s.FriendlyName);
        Set(cmd, "$enabled", s.Enabled);
        Set(cmd, "$consumed", s.Consumed);
        Set(cmd, "$raw", s.Raw);
        cmd.ExecuteNonQuery();
    }

    public void UpsertOrganization(Organization o)
    {
        var cmd = Command("org-upsert", """
            INSERT INTO organization (id, display_name, synced, last_sync, has_p1, raw)
            VALUES ($id, $display_name, $synced, $last_sync, $has_p1, $raw)
            ON CONFLICT(id) DO UPDATE SET
                display_name=excluded.display_name, synced=excluded.synced, last_sync=excluded.last_sync, has_p1=excluded.has_p1, raw=excluded.raw;
            """,
            "$id", "$display_name", "$synced", "$last_sync", "$has_p1", "$raw");
        Set(cmd, "$id", o.Id);
        Set(cmd, "$display_name", o.DisplayName);
        Set(cmd, "$synced", o.OnPremisesSyncEnabled);
        Set(cmd, "$last_sync", o.OnPremisesLastSyncDateTime);
        Set(cmd, "$has_p1", o.HasP1);
        Set(cmd, "$raw", o.Raw);
        cmd.ExecuteNonQuery();
    }

    // ---- deletes / clears / bookkeeping ------------------------------------------------------------------------

    /// <summary>Removes one object (and, for groups, its memberships). Returns true if a row was deleted.</summary>
    public bool Delete(CacheEntity entity, string id)
    {
        if (entity == CacheEntity.Groups)
        {
            ClearGroupMembers(id);
        }

        var idColumn = entity == CacheEntity.Skus ? "sku_id" : "id";
        var cmd = Command($"delete-{entity.Key()}", $"DELETE FROM {entity.Table()} WHERE {idColumn}=$id;", "$id");
        Set(cmd, "$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Deletes every row of an entity, rebuilds its FTS index, clears memberships for groups, and resets <c>sync_state</c>.</summary>
    public void Clear(CacheEntity entity)
    {
        if (entity == CacheEntity.Groups)
        {
            Exec("DELETE FROM group_members;");
        }

        Exec($"DELETE FROM {entity.Table()};");
        if (entity.FtsTable() is { } fts)
        {
            Exec($"INSERT INTO {fts}({fts}) VALUES('rebuild');");
        }

        var cmd = Command("sync-state-delete", "DELETE FROM sync_state WHERE entity=$e;", "$e");
        Set(cmd, "$e", entity.Key());
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes rows of <paramref name="entity"/> that were not upserted by this writer. Every upsert in one
    /// <see cref="CacheDb.Write(Action{CacheWriter})"/> stamps the same <c>updated_at</c>, so "stale" is exactly "not seen in this pass".
    /// Use after a full walk of a no-delta entity (managed devices).
    /// </summary>
    public int DeleteStale(CacheEntity entity)
    {
        if (entity is CacheEntity.Skus or CacheEntity.Organization)
        {
            throw new ArgumentOutOfRangeException(nameof(entity), entity, "table has no updated_at column");
        }

        var cmd = Command($"stale-{entity.Key()}", $"DELETE FROM {entity.Table()} WHERE updated_at <> $now;", "$now");
        Set(cmd, "$now", _now);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>SKUs are few and have no delta; a refresh replaces the table.</summary>
    public void DeleteAllSkus() => Exec("DELETE FROM skus;");

    /// <summary>Reads on this transaction's connection. Never call <see cref="CacheDb"/> readers from inside a write — they open a second connection and block on the table lock.</summary>
    public SyncState? GetSyncState(CacheEntity entity) => CacheDb.ReadSyncState(_conn, _tx, entity);

    public int Count(CacheEntity entity) => CacheDb.ReadCount(_conn, _tx, entity);

    public void SetSyncState(SyncState state)
    {
        var cmd = Command("sync-state-upsert", """
            INSERT INTO sync_state (entity, delta_link, last_full_sync, last_delta_sync, row_count)
            VALUES ($e, $delta, $full, $inc, $n)
            ON CONFLICT(entity) DO UPDATE SET delta_link=excluded.delta_link, last_full_sync=excluded.last_full_sync,
                last_delta_sync=excluded.last_delta_sync, row_count=excluded.row_count;
            """, "$e", "$delta", "$full", "$inc", "$n");
        Set(cmd, "$e", state.Entity.Key());
        Set(cmd, "$delta", state.DeltaLink);
        Set(cmd, "$full", state.LastFullSync);
        Set(cmd, "$inc", state.LastDeltaSync);
        Set(cmd, "$n", state.RowCount);
        cmd.ExecuteNonQuery();
    }

    public void TouchRecent(string objectId, string objectType, DateTimeOffset at)
    {
        var cmd = Command("recent-touch", """
            INSERT INTO recent_objects (object_id, object_type, opened_at, open_count) VALUES ($id, $type, $at, 1)
            ON CONFLICT(object_id) DO UPDATE SET opened_at=excluded.opened_at, open_count=open_count+1, object_type=excluded.object_type;
            """, "$id", "$type", "$at");
        Set(cmd, "$id", objectId);
        Set(cmd, "$type", objectType);
        Set(cmd, "$at", at);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Executes arbitrary SQL inside this transaction (migrations helpers, tests).</summary>
    public int Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var cmd in _commands.Values) cmd.Dispose();
        _commands.Clear();
    }

    private SqliteCommand Command(string key, string sql, params string[] parameters)
    {
        if (_commands.TryGetValue(key, out var existing)) return existing;
        var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        // No explicit SqliteType: the binder infers it from each Value, so integers stay integers.
        foreach (var p in parameters) cmd.Parameters.AddWithValue(p, DBNull.Value);
        cmd.Prepare();
        _commands[key] = cmd;
        return cmd;
    }

    private static void Set(SqliteCommand cmd, string name, string? value) => cmd.Parameters[name].Value = (object?)value ?? DBNull.Value;
    private static void Set(SqliteCommand cmd, string name, bool value) => cmd.Parameters[name].Value = value ? 1L : 0L;
    private static void Set(SqliteCommand cmd, string name, bool? value) => cmd.Parameters[name].Value = value is null ? DBNull.Value : value.Value ? 1L : 0L;
    private static void Set(SqliteCommand cmd, string name, int value) => cmd.Parameters[name].Value = (long)value;
    private static void Set(SqliteCommand cmd, string name, DateTimeOffset? value) => cmd.Parameters[name].Value = value is null ? DBNull.Value : CacheDb.FormatDate(value.Value);
}
