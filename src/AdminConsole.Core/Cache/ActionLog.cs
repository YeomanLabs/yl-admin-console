using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Cache;

/// <summary>
/// The local action log, <c>actions.db</c> (doc 03 §Action log). Separate from the tenant cache so it survives a
/// cache rebuild and spans tenants. Powers the Activity page, Undo toasts and the offboarding "Copy log".
/// </summary>
public sealed class ActionLog : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS actions (
          id               INTEGER PRIMARY KEY AUTOINCREMENT,
          ts               TEXT NOT NULL,
          actor            TEXT NOT NULL,
          tenant_id        TEXT NOT NULL,
          target_type      TEXT NOT NULL,
          target_id        TEXT NOT NULL,
          target_name      TEXT,
          action           TEXT NOT NULL,
          request_summary  TEXT,
          outcome          TEXT NOT NULL,
          message          TEXT,
          graph_request_id TEXT,
          inverse_action   TEXT,
          inverse_payload  TEXT,
          correlation_id   TEXT,
          undone_at        TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_actions_ts ON actions(ts DESC);
        CREATE INDEX IF NOT EXISTS ix_actions_target ON actions(target_id, ts DESC);
        CREATE INDEX IF NOT EXISTS ix_actions_corr ON actions(correlation_id);
        """;

    private const string Columns = "id, ts, actor, tenant_id, target_type, target_id, target_name, action, request_summary, outcome, message, graph_request_id, inverse_action, inverse_payload, correlation_id, undone_at";

    /// <summary>Property names whose values are never persisted (doc 03: never store passwords or tokens).</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwordProfile", "secret", "secretKey", "token", "accessToken", "refreshToken", "key", "recoveryKey", "passwordBase64", "temporaryAccessPass",
    };

    private readonly string _connectionString;
    private readonly ILogger<ActionLog> _logger;
    private readonly SqliteConnection _keeper;
    private readonly object _writeLock = new();

    /// <summary>
    /// The correlation id every row written on this logical flow inherits. AsyncLocal rather than a field because an
    /// offboarding run and an ordinary edit can be in flight at once, and each must stamp only its own rows.
    /// </summary>
    private static readonly AsyncLocal<string?> Correlation = new();

    private ActionLog(string connectionString, ILogger<ActionLog> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
        _keeper = Open();
        using var cmd = _keeper.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    public static ActionLog OpenFile(string path, ILogger<ActionLog>? logger = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        return new ActionLog(cs, logger ?? NullLogger<ActionLog>.Instance);
    }

    public static ActionLog OpenInMemory(ILogger<ActionLog>? logger = null)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = $"actions-{Guid.NewGuid():N}", Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        return new ActionLog(cs, logger ?? NullLogger<ActionLog>.Instance);
    }

    /// <summary>
    /// Groups every row written inside the scope under one id, so a multi-step run (offboarding, create-from-template)
    /// reads as one thing on the Activity page. Steps call the ordinary services, which know nothing about runs.
    /// </summary>
    public static IDisposable BeginCorrelation(string correlationId)
    {
        var previous = Correlation.Value;
        Correlation.Value = correlationId;
        return new CorrelationScope(previous);
    }

    private sealed class CorrelationScope : IDisposable
    {
        private readonly string? _previous;
        public CorrelationScope(string? previous) => _previous = previous;
        public void Dispose() => Correlation.Value = _previous;
    }

    /// <summary>Appends a row and returns its id. Secrets in the summary and inverse payload are redacted first.</summary>
    public long Record(ActionRecord a)
    {
        var ts = a.Timestamp == default ? DateTimeOffset.UtcNow : a.Timestamp;
        var correlation = a.CorrelationId ?? Correlation.Value;
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO actions (ts, actor, tenant_id, target_type, target_id, target_name, action, request_summary, outcome, message,
                                     graph_request_id, inverse_action, inverse_payload, correlation_id, undone_at)
                VALUES ($ts, $actor, $tenant, $ttype, $tid, $tname, $action, $summary, $outcome, $message, $rid, $iaction, $ipayload, $corr, $undone);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ts", CacheDb.FormatDate(ts));
            cmd.Parameters.AddWithValue("$actor", a.Actor);
            cmd.Parameters.AddWithValue("$tenant", a.TenantId);
            cmd.Parameters.AddWithValue("$ttype", a.TargetType.ToString());
            cmd.Parameters.AddWithValue("$tid", a.TargetId);
            cmd.Parameters.AddWithValue("$tname", (object?)a.TargetName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$action", a.Action);
            cmd.Parameters.AddWithValue("$summary", (object?)Redact(a.RequestSummary) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome", a.Outcome.ToString());
            cmd.Parameters.AddWithValue("$message", (object?)a.Message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rid", (object?)a.GraphRequestId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$iaction", (object?)a.InverseAction ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ipayload", (object?)Redact(a.InversePayload) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$corr", (object?)correlation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$undone", a.UndoneAt is null ? DBNull.Value : CacheDb.FormatDate(a.UndoneAt.Value));
            var id = (long)cmd.ExecuteScalar()!;
            _logger.LogInformation("Action {Action} on {TargetType} {TargetId}: {Outcome} (#{Id})", a.Action, a.TargetType, a.TargetId, a.Outcome, id);
            return id;
        }
    }

    public ActionRecord? Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM actions WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public IReadOnlyList<ActionRecord> Query(ActionLogQuery q)
    {
        var where = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        void Add(string clause, string name, object value)
        {
            where.Add(clause);
            cmd.Parameters.AddWithValue(name, value);
        }

        if (q.TargetId is not null) Add("target_id=$tid", "$tid", q.TargetId);
        if (q.TargetType is not null) Add("target_type=$ttype", "$ttype", q.TargetType.Value.ToString());
        if (q.Actor is not null) Add("actor=$actor", "$actor", q.Actor);
        if (q.ActionPrefix is not null) Add("action LIKE $ap ESCAPE '\\'", "$ap", EscapeLike(q.ActionPrefix) + "%");
        if (q.Outcome is not null) Add("outcome=$outcome", "$outcome", q.Outcome.Value.ToString());
        if (q.CorrelationId is not null) Add("correlation_id=$corr", "$corr", q.CorrelationId);
        if (q.TenantId is not null) Add("tenant_id=$tenant", "$tenant", q.TenantId);
        if (q.Since is not null) Add("ts>=$since", "$since", CacheDb.FormatDate(q.Since.Value));
        if (q.Until is not null) Add("ts<=$until", "$until", CacheDb.FormatDate(q.Until.Value));
        if (q.Reversible == true) where.Add("inverse_action IS NOT NULL AND inverse_payload IS NOT NULL AND undone_at IS NULL AND outcome='Success'");
        if (q.Reversible == false) where.Add("NOT (inverse_action IS NOT NULL AND inverse_payload IS NOT NULL AND undone_at IS NULL AND outcome='Success')");

        cmd.CommandText = $"SELECT {Columns} FROM actions" + (where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where)) + " ORDER BY ts DESC, id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", Math.Max(1, q.Limit));

        using var r = cmd.ExecuteReader();
        var list = new List<ActionRecord>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Marks a row as undone so it stops offering Undo. Returns false when the id is unknown or already undone.</summary>
    public bool MarkUndone(long id, DateTimeOffset? at = null)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE actions SET undone_at=$at WHERE id=$id AND undone_at IS NULL;";
            cmd.Parameters.AddWithValue("$at", CacheDb.FormatDate(at ?? DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public long Count()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM actions;";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Copy-as-markdown for the Activity page and the offboarding summary.</summary>
    public static string ToMarkdown(IEnumerable<ActionRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Time (UTC) | Action | Target | Outcome | Message |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var a in records)
        {
            var target = string.IsNullOrEmpty(a.TargetName) ? a.TargetId : $"{a.TargetName} (`{a.TargetId}`)";
            sb.Append("| ").Append(a.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append(" | ").Append(Cell(a.Action))
              .Append(" | ").Append(Cell(target))
              .Append(" | ").Append(a.Outcome)
              .Append(" | ").Append(Cell(a.Message ?? string.Empty))
              .AppendLine(" |");
        }

        return sb.ToString();

        static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Replaces the values of secret-looking JSON properties with <c>***</c>, recursively. Non-JSON text is returned as-is
    /// (callers must not put secrets in free text; the tests pin the JSON contract).
    /// </summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return text;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return text;
        }

        if (node is null) return text;
        var changed = RedactNode(node);
        return changed ? node.ToJsonString() : text;
    }

    private static bool RedactNode(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (SecretKeys.Contains(key))
                    {
                        obj[key] = "***";
                        changed = true;
                    }
                    else if (obj[key] is { } child)
                    {
                        changed |= RedactNode(child);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is not null) changed |= RedactNode(item);
                }

                break;
        }

        return changed;
    }

    public void Dispose() => _keeper.Dispose();

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static ActionRecord Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Timestamp = CacheDb.ReadDate(r, 1) ?? DateTimeOffset.MinValue,
        Actor = r.GetString(2),
        TenantId = r.GetString(3),
        TargetType = Enum.TryParse<ObjectType>(r.GetString(4), out var t) ? t : ObjectType.User,
        TargetId = r.GetString(5),
        TargetName = r.IsDBNull(6) ? null : r.GetString(6),
        Action = r.GetString(7),
        RequestSummary = r.IsDBNull(8) ? null : r.GetString(8),
        Outcome = Enum.TryParse<ActionOutcome>(r.GetString(9), out var o) ? o : ActionOutcome.Failed,
        Message = r.IsDBNull(10) ? null : r.GetString(10),
        GraphRequestId = r.IsDBNull(11) ? null : r.GetString(11),
        InverseAction = r.IsDBNull(12) ? null : r.GetString(12),
        InversePayload = r.IsDBNull(13) ? null : r.GetString(13),
        CorrelationId = r.IsDBNull(14) ? null : r.GetString(14),
        UndoneAt = CacheDb.ReadDate(r, 15),
    };

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
