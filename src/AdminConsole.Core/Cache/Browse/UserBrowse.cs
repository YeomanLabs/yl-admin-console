namespace AdminConsole.Core.Cache.Browse;

/// <summary>
/// The filters and sorts a Users list offers. These are the questions a helpdesk actually asks — "who is disabled",
/// "who has no licence", "who is a guest" — rather than a generic query builder over every column.
/// </summary>
public static class UserBrowse
{
    public static readonly BrowseFilter Enabled =
        new("enabled", "Enabled", "status", "account_enabled = 1", "Can sign in");

    public static readonly BrowseFilter Disabled =
        new("disabled", "Disabled", "status", "account_enabled = 0", "Cannot sign in — usually a leaver");

    public static readonly BrowseFilter Licensed =
        new("licensed", "Licensed", "licence", "licensed = 1", "Has at least one licence assigned");

    public static readonly BrowseFilter Unlicensed =
        new("unlicensed", "Unlicensed", "licence", "licensed = 0", "No licence — often a leaver still enabled, or a starter half set up");

    public static readonly BrowseFilter Members =
        new("members", "Members", "type", "user_type = 'Member'", "Belongs to this tenant");

    public static readonly BrowseFilter Guests =
        new("guests", "Guests", "type", "user_type = 'Guest'", "Invited from outside the tenant");

    public static readonly BrowseFilter CloudOnly =
        new("cloud", "Cloud-only", "origin", "synced = 0", "Created in Entra, so editable here");

    public static readonly BrowseFilter Synced =
        new("synced", "Synced from AD", "origin", "synced = 1", "Mastered on-prem — most attributes are read-only until Phase 2");

    public static IReadOnlyList<BrowseFilter> Filters =>
        [Enabled, Disabled, Licensed, Unlicensed, Members, Guests, CloudOnly, Synced];

    public static IReadOnlyList<BrowseSort> Sorts =>
    [
        new("name", "Name", "display_name"),
        new("upn", "Sign-in name", "upn"),
        new("job", "Job title", "job_title"),
        new("department", "Department", "department"),
        new("created", "Created", "created"),
    ];

    public static BrowseSort DefaultSort => Sorts[0];

    /// <summary>Columns the filter box narrows on. Kept to what an admin would type looking for a person.</summary>
    public static IReadOnlyList<string> TextColumns =>
        ["display_name", "upn", "mail", "job_title", "department", "employee_id"];
}
