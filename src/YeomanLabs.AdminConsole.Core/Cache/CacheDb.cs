using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace YeomanLabs.AdminConsole.Core.Cache;

/// <summary>
/// The per-tenant SQLite cache (docs/03-ARCHITECTURE.md §Cache &amp; sync, docs/05-DATA-MODEL.md). WAL mode, embedded
/// migrations, one keeper connection (so <c>:memory:</c> databases survive), pooled per-call connections for everything else.
/// All methods are synchronous and fast; services own the threading and never call these on the UI thread.
/// </summary>
public sealed partial class CacheDb : IDisposable
{
    private const string MigrationResourcePrefix = "YeomanLabs.AdminConsole.Core.Cache.Migrations.";

    private readonly string _connectionString;
    private readonly ILogger<CacheDb> _logger;
    private readonly SqliteConnection _keeper;
    private readonly object _writeLock = new();
    private bool _disposed;

    private CacheDb(string connectionString, ILogger<CacheDb> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
        _keeper = OpenConnection();
    }

    /// <summary>Opens (creating if needed) the cache file, applies pragmas and pending migrations.</summary>
    public static CacheDb OpenFile(string path, ILogger<CacheDb>? logger = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        var db = new CacheDb(cs, logger ?? NullLogger<CacheDb>.Instance);
        db.Migrate();
        return db;
    }

    /// <summary>A private, shared-cache in-memory database that lives as long as this instance. For tests and the perf harness.</summary>
    public static CacheDb OpenInMemory(string? name = null, ILogger<CacheDb>? logger = null)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = name ?? $"cache-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        var db = new CacheDb(cs, logger ?? NullLogger<CacheDb>.Instance);
        db.Migrate();
        return db;
    }

    /// <summary>Schema version = number of applied migrations (SQLite <c>user_version</c>).</summary>
    public int SchemaVersion
    {
        get
        {
            using var conn = OpenConnection();
            return (int)(long)Scalar(conn, "PRAGMA user_version;")!;
        }
    }

    /// <summary>A new pooled connection with WAL/NORMAL/busy-timeout applied. Caller disposes.</summary>
    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Applies every embedded <c>Cache/Migrations/NNNN_*.sql</c> above the current <c>user_version</c>, each in its own transaction.</summary>
    public void Migrate()
    {
        var assembly = typeof(CacheDb).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Name: n, Number: ParseMigrationNumber(n)))
            .OrderBy(s => s.Number)
            .ToList();

        lock (_writeLock)
        {
            using var conn = OpenConnection();
            var current = (int)(long)Scalar(conn, "PRAGMA user_version;")!;
            foreach (var (name, number) in scripts.Where(s => s.Number > current))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var sql = reader.ReadToEnd();

                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"PRAGMA user_version={number};";
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                _logger.LogInformation("Applied cache migration {Migration}", name[MigrationResourcePrefix.Length..]);
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> inside one write transaction; commits on return, rolls back on exception.</summary>
    public void Write(Action<CacheWriter> work)
    {
        Write(w => { work(w); return 0; });
    }

    /// <summary>Runs <paramref name="work"/> inside one write transaction and returns its result.</summary>
    public T Write<T>(Func<CacheWriter, T> work)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using var writer = new CacheWriter(conn, tx);
            var result = work(writer);
            tx.Commit();
            return result;
        }
    }

    /// <summary>Wipes an entity table (and its FTS index, memberships and sync bookkeeping). Used for 410 delta rebuilds.</summary>
    public void Clear(CacheEntity entity)
    {
        Write(w => w.Clear(entity));
        _logger.LogInformation("Cleared cache entity {Entity}", entity.Key());
    }

    public SyncState? GetSyncState(CacheEntity entity)
    {
        using var conn = OpenConnection();
        return ReadSyncState(conn, null, entity);
    }

    /// <summary>Shared by <see cref="GetSyncState"/> and <see cref="CacheWriter.GetSyncState"/> so a writer never opens a second connection mid-transaction.</summary>
    internal static SyncState? ReadSyncState(SqliteConnection conn, SqliteTransaction? tx, CacheEntity entity)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT delta_link, last_full_sync, last_delta_sync, row_count FROM sync_state WHERE entity=$e;";
        cmd.Parameters.AddWithValue("$e", entity.Key());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SyncState
        {
            Entity = entity,
            DeltaLink = r.IsDBNull(0) ? null : r.GetString(0),
            LastFullSync = ReadDate(r, 1),
            LastDeltaSync = ReadDate(r, 2),
            RowCount = r.GetInt32(3),
        };
    }

    internal static int ReadCount(SqliteConnection conn, SqliteTransaction? tx, CacheEntity entity)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM {entity.Table()};";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<SyncState> GetAllSyncState()
    {
        var list = new List<SyncState>();
        foreach (var entity in Enum.GetValues<CacheEntity>())
        {
            var s = GetSyncState(entity);
            if (s is not null) list.Add(s);
        }

        return list;
    }

    public int Count(CacheEntity entity)
    {
        using var conn = OpenConnection();
        return ReadCount(conn, null, entity);
    }

    /// <summary>The stored Graph JSON for one object, or null.</summary>
    public string? GetRaw(CacheEntity entity, string id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var idColumn = entity == CacheEntity.Skus ? "sku_id" : "id";
        cmd.CommandText = $"SELECT raw FROM {entity.Table()} WHERE {idColumn}=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Records that the admin opened an object; bumps <c>open_count</c> on repeat.</summary>
    public void TouchRecent(string objectId, string objectType, DateTimeOffset? at = null)
    {
        Write(w => w.TouchRecent(objectId, objectType, at ?? DateTimeOffset.UtcNow));
    }

    public IReadOnlyList<RecentObject> GetRecent(int limit = 50)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT object_id, object_type, opened_at, open_count FROM recent_objects ORDER BY opened_at DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<RecentObject>();
        while (r.Read())
        {
            list.Add(new RecentObject
            {
                ObjectId = r.GetString(0),
                ObjectType = r.GetString(1),
                OpenedAt = ReadDate(r, 2) ?? DateTimeOffset.MinValue,
                OpenCount = r.GetInt32(3),
            });
        }

        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keeper.Dispose();
    }

    internal static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset? ReadDate(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null
        : DateTimeOffset.TryParse(r.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) ? dto
        : null;

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static int ParseMigrationNumber(string resourceName)
    {
        var file = resourceName[MigrationResourcePrefix.Length..];
        var digits = new string(file.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new InvalidOperationException($"Migration resource '{resourceName}' must start with a number.");
    }
}
