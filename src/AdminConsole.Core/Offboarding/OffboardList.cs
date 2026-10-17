using System.Text.RegularExpressions;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.Search;

namespace AdminConsole.Core.Offboarding;

/// <summary>
/// Offboard a list (M15.5): the interactive answer to a mail-triggered parser. Names and sign-in names are resolved
/// against the cache by exact match only — a wildcard that matches the wrong person is how a scripted offboarding
/// disables someone who still works there.
/// </summary>
public static partial class OffboardList
{
    private const int SuggestionLimit = 5;

    /// <summary>
    /// One entry per line or semicolon. Commas split a line only when every part is an address, because a pasted
    /// "Vance, Adele" is one person.
    /// </summary>
    public static IReadOnlyList<string> Parse(string text)
    {
        var entries = new List<string>();
        foreach (var raw in text.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1 && parts.All(p => p.Contains('@'))) entries.AddRange(parts.Select(Clean));
            else entries.Add(Clean(raw));
        }

        return [.. entries.Where(e => e.Length > 0)];
    }

    public static IReadOnlyList<ListEntry> Resolve(IEnumerable<string> entries, CacheDb db, SearchService search)
    {
        var resolved = new List<ListEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in entries)
        {
            var exact = db.FindUsersExact(input);

            // "Vance, Adele" is how many HR systems write a name; try it the way Entra stores it before giving up.
            if (exact.Count == 0 && !input.Contains('@') && LastFirst().Match(input) is { Success: true } m)
            {
                exact = db.FindUsersExact($"{m.Groups["first"].Value} {m.Groups["last"].Value}");
            }

            var entry = exact.Count switch
            {
                1 => new ListEntry(input, ListMatch.Found, exact[0], []),
                > 1 => new ListEntry(input, ListMatch.Ambiguous, null, exact),
                _ => new ListEntry(input, ListMatch.NotFound, null, Suggest(input, db, search)),
            };

            if (entry.User is { } user && !seen.Add(user.Id)) entry = entry with { Match = ListMatch.Duplicate };
            resolved.Add(entry);
        }

        return resolved;
    }

    private static IReadOnlyList<DirectoryUser> Suggest(string input, CacheDb db, SearchService search)
    {
        var hits = search.Search(input, SearchScope.Users, SuggestionLimit, CancellationToken.None).Users;
        return [.. hits.Select(h => db.GetUser(h.Id)).OfType<DirectoryUser>()];
    }

    /// <summary>Strips what mail clients add around an address: <c>"Adele Vance" &lt;adele@contoso.com&gt;</c>, mailto:, quotes.</summary>
    private static string Clean(string s)
    {
        if (AngleAddress().Match(s) is { Success: true } a) return a.Groups[1].Value.Trim();
        s = s.Trim().Trim('"', '\'');
        return s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? s[7..] : s;
    }

    [GeneratedRegex(@"<([^<>@\s]+@[^<>\s]+)>")]
    private static partial Regex AngleAddress();

    [GeneratedRegex(@"^(?<last>[^,]+),\s*(?<first>[^,]+)$")]
    private static partial Regex LastFirst();
}
