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

    // Mailbox (M13.4). Each reads the mailboxes table Exchange fills, and only a user with a row there can match: a user
    // Exchange has not been asked about yet is unknown, not mailbox-less, so "No mailbox" must never sweep them in.
    public static readonly BrowseFilter UserMailbox =
        new("mailbox-user", "User mailbox", "mailbox", "id IN (SELECT user_id FROM mailboxes WHERE recipient_type = 'UserMailbox')",
            "A person's own mailbox");

    public static readonly BrowseFilter SharedMailbox =
        new("mailbox-shared", "Shared mailbox", "mailbox", "id IN (SELECT user_id FROM mailboxes WHERE recipient_type = 'SharedMailbox')",
            "A mailbox people share — the account behind it is disabled by design");

    public static readonly BrowseFilter ResourceMailbox =
        new("mailbox-resource", "Room or equipment", "mailbox",
            "id IN (SELECT user_id FROM mailboxes WHERE recipient_type IN ('RoomMailbox','EquipmentMailbox'))",
            "Booked through the calendar rather than signed in to");

    public static readonly BrowseFilter NoMailbox =
        new("mailbox-none", "No mailbox", "mailbox",
            "id IN (SELECT user_id FROM mailboxes WHERE recipient_type NOT IN ('UserMailbox','SharedMailbox','RoomMailbox','EquipmentMailbox','SchedulingMailbox','DiscoveryMailbox'))",
            "No mailbox in this tenant — including guests and people whose mailbox lives elsewhere");

    /// <summary>Every filter, mailbox chips included. Shown once Exchange has been read at least once.</summary>
    public static IReadOnlyList<BrowseFilter> Filters =>
        [Enabled, Disabled, Licensed, Unlicensed, Members, Guests, CloudOnly, Synced, UserMailbox, SharedMailbox, ResourceMailbox, NoMailbox];

    /// <summary>
    /// The filters for an install that has never read Exchange. Mailbox chips there would all count zero, and "No mailbox:
    /// 0" reads as a fact about the tenant when it is really an absence of data.
    /// </summary>
    public static IReadOnlyList<BrowseFilter> FiltersWithoutMailboxes => [.. Filters.Where(f => f.Group != "mailbox")];

    public static IReadOnlyList<BrowseSort> Sorts =>
    [
        new("name", "Name", "display_name"),
        new("upn", "Sign-in name", "upn"),
        new("job", "Job title", "job_title"),
        new("department", "Department", "department"),
        new("created", "Created", "created"),
    ];

    public static BrowseSort DefaultSort => Sorts[0];

    /// <summary>The <c>key=value</c> fields the filter box understands (M18.4). Fixed SQL; the value is always a parameter.</summary>
    public static IReadOnlyList<BrowseField> Fields { get; } =
    [
        BrowseField.Text("dep", "department LIKE @v ESCAPE '\\'", "Department contains", "department"),
        BrowseField.Text("title", "job_title LIKE @v ESCAPE '\\'", "Job title contains", "job", "jobtitle"),
        BrowseField.Text("name", "display_name LIKE @v ESCAPE '\\'", "Display name contains"),
        BrowseField.Text("upn", "upn LIKE @v ESCAPE '\\'", "Sign-in name contains"),
        BrowseField.Text("mail", "mail LIKE @v ESCAPE '\\'", "Email address contains"),
        BrowseField.Text("emp", "employee_id LIKE @v ESCAPE '\\'", "Employee ID contains", "employeeid"),
        BrowseField.Text("lic",
            "EXISTS (SELECT 1 FROM json_each(users.raw, '$.assignedLicenses') j JOIN skus s ON s.sku_id = json_extract(j.value, '$.skuId') " +
            "WHERE s.part_number LIKE @v ESCAPE '\\' OR s.friendly_name LIKE @v ESCAPE '\\')",
            "Has a licence whose name contains (e5, E3, visio)", "licence", "license", "sku"),
        BrowseField.Flag("disabled", "account_enabled = 0", "account_enabled = 1", "Sign-in blocked"),
        BrowseField.Flag("enabled", "account_enabled = 1", "account_enabled = 0", "Sign-in allowed"),
        BrowseField.Flag("guest", "user_type = 'Guest'", "(user_type IS NULL OR user_type <> 'Guest')", "Invited from outside"),
        BrowseField.Flag("synced", "synced = 1", "synced = 0", "Synced from AD"),
        BrowseField.Flag("licensed", "licensed = 1", "licensed = 0", "Has any licence"),
    ];

    /// <summary>Columns the filter box narrows on. Kept to what an admin would type looking for a person.</summary>
    public static IReadOnlyList<string> TextColumns =>
        ["display_name", "upn", "mail", "job_title", "department", "employee_id"];
}
