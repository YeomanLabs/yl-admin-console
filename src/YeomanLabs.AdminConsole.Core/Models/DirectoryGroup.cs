namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>Cache projection of an Entra group (docs/05-DATA-MODEL.md <c>groups</c>).</summary>
public sealed record DirectoryGroup
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Mail { get; init; }
    public required GroupKind Kind { get; init; }
    public required bool IsDynamic { get; init; }
    public required bool OnPremisesSyncEnabled { get; init; }
    public required bool IsAssignableToRole { get; init; }
    public int MemberCount { get; init; }
    public required string Raw { get; init; }
}
