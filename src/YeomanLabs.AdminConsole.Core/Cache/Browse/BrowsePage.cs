namespace YeomanLabs.AdminConsole.Core.Cache.Browse;

/// <summary>
/// One page of a browse list, plus the total the query matched so the UI can say "showing 100 of 4,812" rather than
/// leaving the admin to guess whether there is more.
/// </summary>
public sealed record BrowsePage<T>(IReadOnlyList<T> Rows, int Total, int Skip)
{
    public static BrowsePage<T> Empty => new([], 0, 0);

    /// <summary>True when there are rows after this page.</summary>
    public bool HasMore => Skip + Rows.Count < Total;
}
