namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>
/// Cache projection of an Entra user (docs/05-DATA-MODEL.md <c>users</c>). <see cref="Raw"/> holds the selected Graph JSON
/// for detail views. Parsing (<c>FromJson</c>), badges and the hybrid read-only rule are added in M2.2.
/// </summary>
public sealed record DirectoryUser
{
    public required string Id { get; init; }
    public required string UserPrincipalName { get; init; }
    public string? DisplayName { get; init; }
    public string? Mail { get; init; }
    public string? GivenName { get; init; }
    public string? Surname { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public string? EmployeeId { get; init; }

    /// <summary>Digits only from businessPhones + mobilePhone, for phone search.</summary>
    public string? PhonesDigits { get; init; }

    public required bool AccountEnabled { get; init; }
    public string? UserType { get; init; }
    public required bool OnPremisesSyncEnabled { get; init; }
    public required bool Licensed { get; init; }
    public DateTimeOffset? CreatedDateTime { get; init; }
    public required string Raw { get; init; }
}
