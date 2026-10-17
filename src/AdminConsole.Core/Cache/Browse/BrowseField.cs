namespace AdminConsole.Core.Cache.Browse;

/// <summary>
/// One <c>key=value</c> a list's filter box understands (M18.4): <c>dep=research</c>, <c>disabled=true</c>.
/// </summary>
/// <remarks>
/// The same rule as <see cref="BrowseFilter"/> (9.1): the SQL is a constant written here, never built from what an admin
/// types. A text field's fragment holds one parameter, <c>@v</c>, which the value is bound to; a yes/no field picks one of
/// two constant fragments. So the set of SQL that can reach the database is exactly what these catalogs contain.
/// </remarks>
public sealed record BrowseField
{
    public required string Key { get; init; }

    /// <summary>Other names that mean the same key: <c>department</c> for <c>dep</c>.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    public required string Description { get; init; }

    /// <summary>For a text field: the fragment, with <c>@v</c> where the bound value goes. Values match anywhere (LIKE %v%).</summary>
    public string? TextSql { get; init; }

    /// <summary>For a yes/no field: the fragment for true; <see cref="FalseSql"/> for false.</summary>
    public string? TrueSql { get; init; }

    public string? FalseSql { get; init; }

    public bool IsBoolean => TrueSql is not null;

    /// <summary>A text field whose value must match whole (still case-insensitive): <c>compliance=compliant</c> must not
    /// also find <c>noncompliant</c>.</summary>
    public bool Exact { get; init; }

    public bool Matches(string key) =>
        string.Equals(Key, key, StringComparison.OrdinalIgnoreCase) || Aliases.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static BrowseField Text(string key, string sql, string description, params string[] aliases) =>
        new() { Key = key, TextSql = sql, Description = description, Aliases = aliases };

    public static BrowseField Whole(string key, string sql, string description, params string[] aliases) =>
        new() { Key = key, TextSql = sql, Description = description, Aliases = aliases, Exact = true };

    public static BrowseField Flag(string key, string whenTrue, string whenFalse, string description, params string[] aliases) =>
        new() { Key = key, TrueSql = whenTrue, FalseSql = whenFalse, Description = description, Aliases = aliases };
}
