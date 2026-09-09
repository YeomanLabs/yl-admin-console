using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Tests.Cache;

public sealed class CacheDbTests : IDisposable
{
    private readonly CacheDb _db = CacheDb.OpenInMemory();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Migrate_CreatesSchemaOnce_AndIsIdempotent()
    {
        _db.SchemaVersion.Should().Be(1);
        _db.Migrate();
        _db.SchemaVersion.Should().Be(1);

        Tables().Should().Contain(["sync_state", "users", "users_fts", "groups", "groups_fts", "group_members", "devices", "managed_devices", "devices_fts", "skus", "recent_objects", "organization"]);
        Scalar("PRAGMA journal_mode;").Should().Be("memory", "in-memory databases cannot use WAL; the pragma is still issued for files");
    }

    [Fact]
    public void Fts5_IsAvailableInBundledSqlite()
    {
        Scalar("SELECT sqlite_compileoption_used('ENABLE_FTS5');").Should().Be(1L);
    }

    [Fact]
    public void UpsertUser_TwiceKeepsRowidAndFtsInSync()
    {
        _db.Write(w => w.UpsertUser(User("u1", "ada@contoso.com", "Ada Lovelace")));
        var rowid1 = (long)Scalar("SELECT rowid FROM users WHERE id='u1';")!;

        _db.Write(w => w.UpsertUser(User("u1", "ada@contoso.com", "Ada Byron")));
        var rowid2 = (long)Scalar("SELECT rowid FROM users WHERE id='u1';")!;

        rowid2.Should().Be(rowid1, "ON CONFLICT DO UPDATE must not reassign the rowid the FTS index points at");
        _db.Count(CacheEntity.Users).Should().Be(1);
        FtsIds("users_fts", "byron").Should().Equal("u1");
        FtsIds("users_fts", "lovelace").Should().BeEmpty("the old tokens were removed by the update trigger");
        _db.GetRaw(CacheEntity.Users, "u1").Should().Contain("Ada Byron");
    }

    [Fact]
    public void DeleteUser_RemovesFromFts()
    {
        _db.Write(w => w.UpsertUser(User("u1", "ada@contoso.com", "Ada Lovelace")));
        _db.Write(w => w.Delete(CacheEntity.Users, "u1")).Should().BeTrue();
        _db.Write(w => w.Delete(CacheEntity.Users, "u1")).Should().BeFalse();
        FtsIds("users_fts", "ada").Should().BeEmpty();
    }

    [Fact]
    public void Clear_WipesRowsFtsMembershipsAndSyncState()
    {
        _db.Write(w =>
        {
            w.UpsertGroup(Group("g1", "Sales"));
            w.AddGroupMember("g1", "u1", "user");
            w.RefreshMemberCount("g1");
            w.SetSyncState(new SyncState { Entity = CacheEntity.Groups, DeltaLink = "https://x/delta", RowCount = 1, LastFullSync = DateTimeOffset.UtcNow });
        });
        Scalar("SELECT member_count FROM groups WHERE id='g1';").Should().Be(1L);
        _db.GetSyncState(CacheEntity.Groups)!.DeltaLink.Should().Be("https://x/delta");

        _db.Clear(CacheEntity.Groups);

        _db.Count(CacheEntity.Groups).Should().Be(0);
        Scalar("SELECT COUNT(*) FROM group_members;").Should().Be(0L);
        FtsIds("groups_fts", "sales").Should().BeEmpty();
        _db.GetSyncState(CacheEntity.Groups).Should().BeNull();
    }

    [Fact]
    public void GroupUpsert_PreservesMemberCountAcrossMetadataRefresh()
    {
        _db.Write(w =>
        {
            w.UpsertGroup(Group("g1", "Sales"));
            w.AddGroupMember("g1", "u1", "user");
            w.AddGroupMember("g1", "u2", "user");
            w.RefreshMemberCount("g1");
        });
        _db.Write(w => w.UpsertGroup(Group("g1", "Sales (renamed)")));

        Scalar("SELECT member_count FROM groups WHERE id='g1';").Should().Be(2L);
        _db.Write(w => w.RemoveGroupMember("g1", "u2"));
        _db.Write(w => w.RefreshMemberCount());
        Scalar("SELECT member_count FROM groups WHERE id='g1';").Should().Be(1L);
    }

    [Fact]
    public void Write_RollsBackOnException()
    {
        var act = () => _db.Write(w =>
        {
            w.UpsertUser(User("u1", "a@b", "A"));
            throw new InvalidOperationException("boom");
        });

        act.Should().Throw<InvalidOperationException>();
        _db.Count(CacheEntity.Users).Should().Be(0);
    }

    [Fact]
    public void SyncState_RoundTripsDatesAsUtcIso8601()
    {
        var full = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.FromHours(-7));
        var inc = new DateTimeOffset(2026, 9, 9, 18, 30, 0, TimeSpan.Zero);
        _db.Write(w => w.SetSyncState(new SyncState { Entity = CacheEntity.Users, DeltaLink = "d", LastFullSync = full, LastDeltaSync = inc, RowCount = 42 }));

        var s = _db.GetSyncState(CacheEntity.Users)!;
        s.DeltaLink.Should().Be("d");
        s.LastFullSync.Should().Be(full);
        s.LastFullSync!.Value.Offset.Should().Be(TimeSpan.Zero);
        s.LastDeltaSync.Should().Be(inc);
        s.LastSync.Should().Be(inc);
        s.RowCount.Should().Be(42);
        _db.GetAllSyncState().Should().ContainSingle();
    }

    [Fact]
    public void Recent_TouchIncrementsCountAndOrdersByRecency()
    {
        var t0 = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        _db.TouchRecent("u1", "user", t0);
        _db.TouchRecent("d1", "device", t0.AddMinutes(1));
        _db.TouchRecent("u1", "user", t0.AddMinutes(2));

        var recent = _db.GetRecent();
        recent.Select(r => r.ObjectId).Should().Equal("u1", "d1");
        recent[0].OpenCount.Should().Be(2);
        recent[1].OpenCount.Should().Be(1);
    }

    [Fact]
    public void AllEntityUpserts_WriteRowsAndRawIsReadable()
    {
        _db.Write(w =>
        {
            w.UpsertDevice(new EntraDevice { Id = "e1", DeviceId = "aad-1", DisplayName = "PC-1", IsCompliant = true, Raw = "{\"id\":\"e1\"}" });
            w.UpsertManagedDevice(new ManagedDevice { Id = "m1", AzureAdDeviceId = "aad-1", DeviceName = "PC-1", SerialNumber = "SN12345", Raw = "{\"id\":\"m1\"}" });
            w.UpsertSku(new LicenseSku { SkuId = "s1", SkuPartNumber = "SPE_E3", FriendlyName = "Microsoft 365 E3", Enabled = 10, Consumed = 3, Raw = "{}" });
            w.UpsertOrganization(new Organization { Id = "t1", DisplayName = "Contoso", HasP1 = true, Raw = "{}" });
        });

        _db.Count(CacheEntity.Devices).Should().Be(1);
        _db.Count(CacheEntity.ManagedDevices).Should().Be(1);
        _db.Count(CacheEntity.Skus).Should().Be(1);
        _db.Count(CacheEntity.Organization).Should().Be(1);
        _db.GetRaw(CacheEntity.Skus, "s1").Should().Be("{}");
        Scalar("SELECT compliant FROM devices WHERE id='e1';").Should().Be(1L);
        FtsIds("devices_fts", "sn12345", "managed_devices").Should().Equal("m1");
    }

    [Fact]
    public void OpenFile_CreatesDatabaseInWalMode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yl-cache-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var file = CacheDb.OpenFile(path))
            {
                file.SchemaVersion.Should().Be(1);
                using var conn = file.OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode;";
                cmd.ExecuteScalar().Should().Be("wal");
            }

            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(f)) File.Delete(f);
            }
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static DirectoryUser User(string id, string upn, string name) => new()
    {
        Id = id,
        UserPrincipalName = upn,
        DisplayName = name,
        AccountEnabled = true,
        OnPremisesSyncEnabled = false,
        Licensed = true,
        Raw = $"{{\"id\":\"{id}\",\"displayName\":\"{name}\"}}",
    };

    private static DirectoryGroup Group(string id, string name) => new()
    {
        Id = id,
        DisplayName = name,
        Kind = GroupKind.Security,
        IsDynamic = false,
        OnPremisesSyncEnabled = false,
        IsAssignableToRole = false,
        Raw = "{}",
    };

    private object? Scalar(string sql)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private List<string> Tables()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private List<string> FtsIds(string fts, string term, string? baseTable = null)
    {
        baseTable ??= fts[..^4];
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT b.id FROM {fts} f JOIN {baseTable} b ON b.rowid = f.rowid WHERE {fts} MATCH $q;";
        cmd.Parameters.AddWithValue("$q", term);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
