namespace YeomanLabs.AdminConsole.Core.Cache;

/// <summary>One row of <c>recent_objects</c>; feeds the palette's recent-object boost.</summary>
public sealed record RecentObject
{
    public required string ObjectId { get; init; }
    public required string ObjectType { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public required int OpenCount { get; init; }
}
