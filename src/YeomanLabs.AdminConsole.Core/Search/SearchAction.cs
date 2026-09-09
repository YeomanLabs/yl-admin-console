namespace YeomanLabs.AdminConsole.Core.Search;

/// <summary>A palette command (UX §3 "Actions" group). The App registers these; Core only matches names and aliases.</summary>
public sealed record SearchAction
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string? Description { get; init; }
}
