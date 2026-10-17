using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;

using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Search;

/// <summary>
/// The command palette's search (doc 03 §Search, UX §3). FTS5 <c>MATCH</c> with prefix tokens takes the best-ranked rows per
/// table by <c>bm25</c> (100 candidates); the rows are then re-ranked in memory (prefix on primary &gt; prefix on UPN/device name &gt;
/// word-prefix &gt; substring &gt; FTS-only) with a boost for recently opened objects. Hits the cache only, never Graph.
/// </summary>
public sealed class SearchService
{
    // Doc 03 says 40; 100 costs nothing measurable and keeps a prefix match from being crowded out when many rows share a token.
    private const int FtsCandidates = 100;
    private const double TierPrimaryPrefix = 1000;
    private const double TierSecondaryPrefix = 800;
    private const double TierWordPrefix = 600;
    private const double TierSubstring = 400;
    private const double TierFtsOnly = 200;
    private const double RecentBoost = 150;
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    private readonly CacheDb _db;
    private readonly IReadOnlyList<SearchAction> _actions;
    private readonly TimeProvider _time;

    public SearchService(CacheDb db, IReadOnlyList<SearchAction>? actions = null, TimeProvider? time = null)
    {
        _db = db;
        _actions = actions ?? [];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Runs on the thread pool; the caller (a ViewModel) just awaits.</summary>
    public Task<SearchResults> SearchAsync(string query, SearchScope scope, int limitPerGroup, CancellationToken ct) =>
        Task.Run(() => Search(query, scope, limitPerGroup, ct), ct);

    /// <summary>Synchronous core, for tests and the perf gate.</summary>
    public SearchResults Search(string query, SearchScope scope, int limitPerGroup, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var parsed = Parse(query, scope);
        if (parsed.IsEmpty)
        {
            // Nothing typed: the palette shows its empty-state caption — except a bare ">" which lists every action.
            var all = parsed.Scope == SearchScope.Actions ? SearchActions(parsed, limitPerGroup) : [];
            return new SearchResults { Query = parsed, Actions = all, ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds };
        }

        using var conn = _db.OpenConnection();
        var recent = LoadRecent(conn);
        var users = Wants(parsed.Scope, SearchScope.Users) ? SearchUsers(conn, parsed, recent, limitPerGroup, ct) : [];
        var devices = Wants(parsed.Scope, SearchScope.Devices) ? SearchDevices(conn, parsed, recent, limitPerGroup, ct) : [];
        var groups = Wants(parsed.Scope, SearchScope.Groups) ? SearchGroups(conn, parsed, recent, limitPerGroup, ct) : [];
        var actions = Wants(parsed.Scope, SearchScope.Actions) ? SearchActions(parsed, limitPerGroup) : [];

        return new SearchResults
        {
            Query = parsed,
            Users = users,
            Devices = devices,
            Groups = groups,
            Actions = actions,
            ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>Applies the <c>u:</c> / <c>d:</c> / <c>g:</c> / <c>&gt;</c> prefixes and normalizes the text.</summary>
    public static ParsedQuery Parse(string query, SearchScope scope)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.StartsWith('>'))
        {
            scope = SearchScope.Actions;
            text = text[1..].Trim();
        }
        else if (text.Length >= 2 && text[1] == ':' && "udg".Contains(char.ToLowerInvariant(text[0])))
        {
            scope = char.ToLowerInvariant(text[0]) switch { 'u' => SearchScope.Users, 'd' => SearchScope.Devices, _ => SearchScope.Groups };
            text = text[2..].Trim();
        }

        text = text.ToLowerInvariant();
        var terms = Tokenize(text);
        var digits = JsonProps.DigitsOnly(text);
        var looksLikeNumber = digits.Length >= 3 && text.All(c => char.IsDigit(c) || c is '+' or ' ' or '-' or '(' or ')' or '.');

        return new ParsedQuery
        {
            Original = query ?? string.Empty,
            Scope = scope,
            Text = text,
            Terms = terms,
            Digits = looksLikeNumber ? digits : null,
        };
    }

    /// <summary>Splits on anything unicode61 would split on (non-letter/digit), which keeps "ada@contoso.com" → ada, contoso, com.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var terms = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                terms.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0) terms.Add(sb.ToString());
        return terms;
    }

    /// <summary>FTS5 query: every term as a quoted prefix token, implicitly ANDed. Quotes inside terms are impossible after tokenizing.</summary>
    public static string BuildMatch(IReadOnlyList<string> terms, string? column = null)
    {
        var body = string.Join(" ", terms.Select(t => $"\"{t}\"*"));
        return column is null ? body : $"{column}: ({body})";
    }

    // ---- per-table searches ------------------------------------------------------------------------------------

    private List<SearchHit> SearchUsers(SqliteConnection conn, ParsedQuery q, Dictionary<string, RecentObject> recent, int limit, CancellationToken ct)
    {
        var hits = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        const string select = "SELECT u.id, u.display_name, u.upn, u.mail, u.account_enabled, u.synced, u.user_type, bm25(users_fts) AS rank, mb.recipient_type " +
                              "FROM users_fts JOIN users u ON u.rowid = users_fts.rowid LEFT JOIN mailboxes mb ON mb.user_id = u.id " +
                              "WHERE users_fts MATCH $q ORDER BY rank LIMIT $n;";

        if (q.Terms.Count > 0)
        {
            Run(conn, select, BuildMatch(q.Terms), r => AddUser(hits, r, q, recent), ct);
        }

        if (q.Digits is not null)
        {
            // Phone / employee-ID fragment: prefix on the digits columns.
            Run(conn, select, $"phones_digits: \"{q.Digits}\"* OR employee_id: \"{q.Digits}\"*", r => AddUser(hits, r, q, recent), ct);
        }

        return Top(hits.Values, limit);
    }

    private void AddUser(Dictionary<string, SearchHit> hits, SqliteDataReader r, ParsedQuery q, Dictionary<string, RecentObject> recent)
    {
        var id = r.GetString(0);
        if (hits.ContainsKey(id)) return;
        var name = r.IsDBNull(1) ? null : r.GetString(1);
        var upn = r.GetString(2);
        var mail = r.IsDBNull(3) ? null : r.GetString(3);
        var badges = new List<string>(3);
        var mailbox = MailboxKinds.From(r.IsDBNull(8) ? null : r.GetString(8));
        if (MailboxKinds.Badge(mailbox) is { } mailboxBadge && mailbox is not MailboxKind.None) badges.Add(mailboxBadge);
        if (r.GetInt64(4) == 0 && !MailboxKinds.IsDisabledByDesign(mailbox)) badges.Add("Disabled");
        if (r.GetInt64(5) != 0) badges.Add("Synced");
        if (!r.IsDBNull(6) && string.Equals(r.GetString(6), "Guest", StringComparison.OrdinalIgnoreCase)) badges.Add("Guest");

        var tier = Tier(q, primary: name, secondary: upn, extra: mail);
        hits[id] = new SearchHit
        {
            ObjectType = ObjectType.User,
            Id = id,
            Primary = name ?? upn,
            Secondary = upn,
            Badges = badges,
            Score = tier + Recency(recent, id) - r.GetDouble(7) * 0.01,
        };
    }

    private List<SearchHit> SearchDevices(SqliteConnection conn, ParsedQuery q, Dictionary<string, RecentObject> recent, int limit, CancellationToken ct)
    {
        var hits = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        const string columns = "m.id, m.device_name, m.serial, m.model, m.compliance, m.encrypted, m.primary_user_name";
        const string select = $"SELECT {columns}, bm25(devices_fts) AS rank FROM devices_fts JOIN managed_devices m ON m.rowid = devices_fts.rowid " +
                              "WHERE devices_fts MATCH $q ORDER BY rank LIMIT $n;";

        if (q.Terms.Count > 0)
        {
            Run(conn, select, BuildMatch(q.Terms), r => AddDevice(hits, r, q, recent, suffixHit: false), ct);
        }

        // Serial suffix: techs read the last characters off the chassis label. FTS cannot do suffixes; a LIKE scan of
        // ~2k rows costs about a millisecond.
        if (q.Terms.Count == 1 && q.Terms[0].Length >= 3)
        {
            const string suffix = $"SELECT {columns}, 0.0 AS rank FROM managed_devices m WHERE m.serial LIKE $q ESCAPE '\\' LIMIT $n;";
            Run(conn, suffix, "%" + EscapeLike(q.Terms[0]), r => AddDevice(hits, r, q, recent, suffixHit: true), ct);
        }

        return Top(hits.Values, limit);
    }

    private void AddDevice(Dictionary<string, SearchHit> hits, SqliteDataReader r, ParsedQuery q, Dictionary<string, RecentObject> recent, bool suffixHit)
    {
        var id = r.GetString(0);
        if (hits.ContainsKey(id)) return;
        var name = r.IsDBNull(1) ? null : r.GetString(1);
        var serial = r.IsDBNull(2) ? null : r.GetString(2);
        var model = r.IsDBNull(3) ? null : r.GetString(3);
        var compliance = r.IsDBNull(4) ? null : r.GetString(4);
        var badges = new List<string>(2);
        if (string.Equals(compliance, "noncompliant", StringComparison.OrdinalIgnoreCase)) badges.Add("Non-compliant");
        if (!r.IsDBNull(5) && r.GetInt64(5) == 0) badges.Add("Not encrypted");

        var tier = suffixHit ? TierSubstring : Tier(q, primary: name, secondary: serial, extra: r.IsDBNull(6) ? null : r.GetString(6));
        var secondary = string.Join(" · ", new[] { serial, model }.Where(s => !string.IsNullOrEmpty(s)));
        hits[id] = new SearchHit
        {
            ObjectType = ObjectType.Device,
            Id = id,
            Primary = name ?? serial ?? id,
            Secondary = secondary.Length == 0 ? null : secondary,
            Badges = badges,
            Score = tier + Recency(recent, id) - r.GetDouble(7) * 0.01,
        };
    }

    private List<SearchHit> SearchGroups(SqliteConnection conn, ParsedQuery q, Dictionary<string, RecentObject> recent, int limit, CancellationToken ct)
    {
        if (q.Terms.Count == 0) return [];
        var hits = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        const string select = "SELECT g.id, g.display_name, g.mail, g.kind, g.dynamic, g.synced, g.member_count, bm25(groups_fts) AS rank " +
                              "FROM groups_fts JOIN groups g ON g.rowid = groups_fts.rowid WHERE groups_fts MATCH $q ORDER BY rank LIMIT $n;";
        Run(conn, select, BuildMatch(q.Terms), r =>
        {
            var id = r.GetString(0);
            if (hits.ContainsKey(id)) return;
            var name = r.IsDBNull(1) ? null : r.GetString(1);
            var mail = r.IsDBNull(2) ? null : r.GetString(2);
            var kind = r.GetString(3);
            var badges = new List<string>(3) { kind == nameof(GroupKind.M365) ? "M365" : kind == nameof(GroupKind.Security) ? "Security" : "Distribution" };
            if (r.GetInt64(4) != 0) badges.Add("Dynamic");
            if (r.GetInt64(5) != 0) badges.Add("Synced");
            var members = r.GetInt64(6);

            hits[id] = new SearchHit
            {
                ObjectType = ObjectType.Group,
                Id = id,
                Primary = name ?? mail ?? id,
                Secondary = members == 1 ? "1 member" : $"{members} members",
                Badges = badges,
                Score = Tier(q, primary: name, secondary: mail, extra: null) + Recency(recent, id) - r.GetDouble(7) * 0.01,
            };
        }, ct);

        return Top(hits.Values, limit);
    }

    private List<SearchHit> SearchActions(ParsedQuery q, int limit)
    {
        if (q.Terms.Count == 0 && q.Scope != SearchScope.Actions) return [];
        var hits = new List<SearchHit>();
        foreach (var a in _actions)
        {
            var names = new[] { a.Title }.Concat(a.Aliases).Select(n => n.ToLowerInvariant()).ToList();
            double tier;
            if (q.Text.Length == 0) tier = TierFtsOnly;
            else if (names.Any(n => n.StartsWith(q.Text, StringComparison.Ordinal))) tier = TierPrimaryPrefix;
            else if (names.Any(n => Tokenize(n).Any(w => w.StartsWith(q.Terms[0], StringComparison.Ordinal)))) tier = TierWordPrefix;
            else if (names.Any(n => n.Contains(q.Text, StringComparison.Ordinal))) tier = TierSubstring;
            else continue;

            hits.Add(new SearchHit { ObjectType = ObjectType.Action, Id = a.Id, Primary = a.Title, Secondary = a.Description, Score = tier });
        }

        return Top(hits, limit);
    }

    // ---- ranking ---------------------------------------------------------------------------------------------------

    /// <summary>UX §3: prefix on displayName &gt; prefix on UPN/deviceName &gt; word-prefix &gt; substring &gt; FTS-only.</summary>
    private static double Tier(ParsedQuery q, string? primary, string? secondary, string? extra)
    {
        var text = q.Text;
        if (text.Length == 0) return TierFtsOnly;
        var p = primary?.ToLowerInvariant();
        var s = secondary?.ToLowerInvariant();
        var x = extra?.ToLowerInvariant();

        if (p is not null && p.StartsWith(text, StringComparison.Ordinal)) return TierPrimaryPrefix;
        if (s is not null && s.StartsWith(text, StringComparison.Ordinal)) return TierSecondaryPrefix;
        if (x is not null && x.StartsWith(text, StringComparison.Ordinal)) return TierSecondaryPrefix;
        if (q.Terms.Count > 0)
        {
            var first = q.Terms[0];
            if (p is not null && Tokenize(p).Any(w => w.StartsWith(first, StringComparison.Ordinal))) return TierWordPrefix;
            if (s is not null && Tokenize(s).Any(w => w.StartsWith(first, StringComparison.Ordinal))) return TierWordPrefix - 50;
        }

        if (p is not null && p.Contains(text, StringComparison.Ordinal)) return TierSubstring;
        if (s is not null && s.Contains(text, StringComparison.Ordinal)) return TierSubstring - 50;
        return TierFtsOnly;
    }

    private double Recency(Dictionary<string, RecentObject> recent, string id)
    {
        if (!recent.TryGetValue(id, out var r)) return 0;
        if (_time.GetUtcNow() - r.OpenedAt > RecentWindow) return 0;
        return RecentBoost + Math.Min(50, r.OpenCount * 5);
    }

    private static List<SearchHit> Top(IEnumerable<SearchHit> hits, int limit) =>
        hits.OrderByDescending(h => h.Score).ThenBy(h => h.Primary, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();

    private static Dictionary<string, RecentObject> LoadRecent(SqliteConnection conn)
    {
        var dict = new Dictionary<string, RecentObject>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT object_id, object_type, opened_at, open_count FROM recent_objects ORDER BY opened_at DESC LIMIT 200;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            dict[r.GetString(0)] = new RecentObject
            {
                ObjectId = r.GetString(0),
                ObjectType = r.GetString(1),
                OpenedAt = CacheDb.ReadDate(r, 2) ?? DateTimeOffset.MinValue,
                OpenCount = r.GetInt32(3),
            };
        }

        return dict;
    }

    private static void Run(SqliteConnection conn, string sql, string q, Action<SqliteDataReader> row, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$q", q);
        cmd.Parameters.AddWithValue("$n", FtsCandidates);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            row(r);
        }
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static bool Wants(SearchScope scope, SearchScope group) => scope == SearchScope.All || scope == group;
}
