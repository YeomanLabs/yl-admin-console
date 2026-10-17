using AdminConsole.Core.Models;

namespace AdminConsole.Core.Users.BulkEdit;

/// <summary>One person in a bulk edit's preview.</summary>
public sealed record BulkPlanRow(DirectoryUser User, IReadOnlyList<BulkPlannedChange> Changes)
{
    public string Label => User.DisplayName ?? User.UserPrincipalName;

    public IReadOnlyList<BulkPlannedChange> ToEntra => [.. Changes.Where(c => c.Route == BulkRoute.Entra)];

    public IReadOnlyList<BulkPlannedChange> ToDirectory => [.. Changes.Where(c => c.Route == BulkRoute.ActiveDirectory)];

    public bool HasWork => Changes.Any(c => c.Route is BulkRoute.Entra or BulkRoute.ActiveDirectory);
}
