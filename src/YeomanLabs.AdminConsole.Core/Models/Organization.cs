namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>Cache projection of the tenant <c>organization</c> object (docs/05-DATA-MODEL.md <c>organization</c>).</summary>
public sealed record Organization
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public bool OnPremisesSyncEnabled { get; init; }
    public DateTimeOffset? OnPremisesLastSyncDateTime { get; init; }
    public bool HasP1 { get; init; }
    public required string Raw { get; init; }
}
