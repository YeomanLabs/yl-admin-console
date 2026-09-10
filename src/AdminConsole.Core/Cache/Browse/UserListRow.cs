namespace AdminConsole.Core.Cache.Browse;

/// <summary>
/// One user as a list page shows them. Deliberately not <c>DirectoryUser</c>: a list draws a few columns for hundreds
/// of rows, and parsing every row's <c>raw</c> JSON to render a name would make paging cost what the detail page costs.
/// </summary>
public sealed record UserListRow
{
    public required string Id { get; init; }
    public required string Upn { get; init; }
    public string? DisplayName { get; init; }
    public string? Mail { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public bool Enabled { get; init; }
    public bool Licensed { get; init; }
    public bool Synced { get; init; }
    public string? UserType { get; init; }
    public DateTimeOffset? Created { get; init; }

    public bool IsGuest => string.Equals(UserType, "Guest", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to show when the display name is missing, so a row is never blank.</summary>
    public string Primary => string.IsNullOrWhiteSpace(DisplayName) ? Upn : DisplayName;
}
