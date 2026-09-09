namespace YeomanLabs.AdminConsole.Core.Cache.Browse;

/// <summary>
/// One toggleable filter on a list page: what the chip says, and the SQL that narrows to it.
/// </summary>
/// <remarks>
/// <para>
/// The SQL is a constant written in the entity's catalog, never assembled from anything an admin types — free text is
/// always a parameter. A filter is only reachable by its <see cref="Key"/>, and an unknown key is dropped, so the set
/// of fragments that can ever reach the database is exactly what those catalogs contain.
/// </para>
/// <para>
/// Filters sharing a <see cref="Group"/> are OR-ed; the groups are then AND-ed. Ticking both Enabled and Disabled
/// therefore shows everyone, which is what a chip filter is expected to do — the alternative reads as broken, because
/// ticking a second box would empty the list.
/// </para>
/// </remarks>
public sealed record BrowseFilter(string Key, string Label, string Group, string Sql, string Description);
