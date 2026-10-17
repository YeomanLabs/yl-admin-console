namespace AdminConsole.Core.Reports;

/// <summary>A licensed account worth a look: sign-in blocked, or quiet for 90 days (M19.2).</summary>
public sealed record LicensedUserRow
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string Upn { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyList<string> Licences { get; init; } = [];

    /// <summary>"Direct", "Group" or "Direct and group", per licence, in the order of <see cref="Licences"/>.</summary>
    public IReadOnlyList<string> AssignedBy { get; init; } = [];

    public DateTimeOffset? LastSignIn { get; init; }
    public DateTimeOffset? Created { get; init; }
    public bool IsQuiet { get; init; }

    public string Reason => !Enabled
        ? "Sign-in blocked but still licensed"
        : LastSignIn is { } at ? $"No sign-in since {at.ToLocalTime():d}" : "Never signed in";

    public string LicencesText => string.Join(", ", Licences.Select((l, i) => i < AssignedBy.Count && AssignedBy[i] != "Direct" ? $"{l} ({AssignedBy[i].ToLowerInvariant()})" : l));
}
