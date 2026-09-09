namespace YeomanLabs.AdminConsole.Core.Cache.Browse;

/// <summary>One sortable column: what the header says, and what to ORDER BY.</summary>
/// <remarks><paramref name="Sql"/> is a constant for the same reason.</remarks>
public sealed record BrowseSort(string Key, string Label, string Sql);
