namespace AdminConsole.Core.Cache.Browse;

/// <summary>The filters and sorts a Groups list offers, chosen around what makes a group editable or not.</summary>
public static class GroupBrowse
{
    /// <summary>The <c>key=value</c> fields the filter box understands (M18.4).</summary>
    public static IReadOnlyList<BrowseField> Fields { get; } =
    [
        BrowseField.Text("name", "display_name LIKE @v ESCAPE '\\'", "Name contains"),
        BrowseField.Text("mail", "mail LIKE @v ESCAPE '\\'", "Email address contains"),
        BrowseField.Text("kind", "kind LIKE @v ESCAPE '\\'", "Security, M365 or Distribution"),
        BrowseField.Flag("dynamic", "dynamic = 1", "dynamic = 0", "Membership from a rule"),
        BrowseField.Flag("synced", "synced = 1", "synced = 0", "Synced from AD"),
        BrowseField.Flag("empty", "member_count = 0", "member_count > 0", "No members"),
        BrowseField.Flag("roleassignable", "role_assignable = 1", "role_assignable = 0", "Can hold a directory role", "role"),
    ];

    public static readonly BrowseFilter Security =
        new("security", "Security", "kind", "kind = 'Security'", "Used for access, not mail");

    public static readonly BrowseFilter Microsoft365 =
        new("m365", "Microsoft 365", "kind", "kind = 'M365'", "Has a mailbox, a site and a team");

    public static readonly BrowseFilter Distribution =
        new("distribution", "Distribution", "kind", "kind = 'DistributionOrMailSecurity'", "Mail distribution or mail-enabled security");

    public static readonly BrowseFilter Assigned =
        new("assigned", "Assigned", "membership", "dynamic = 0", "Members are added by hand, so they can be changed here");

    public static readonly BrowseFilter Dynamic =
        new("dynamic", "Dynamic", "membership", "dynamic = 1", "Membership comes from a rule — members cannot be added or removed");

    public static readonly BrowseFilter CloudOnly =
        new("cloud", "Cloud-only", "origin", "synced = 0", "Created in Entra, so editable here");

    public static readonly BrowseFilter Synced =
        new("synced", "Synced from AD", "origin", "synced = 1", "Mastered on-prem — membership is changed in AD until Phase 2");

    public static readonly BrowseFilter RoleAssignable =
        new("roleassignable", "Role-assignable", "role", "role_assignable = 1", "Can hold a directory role, so changing it needs Privileged Role Administrator");

    public static readonly BrowseFilter Empty =
        new("empty", "Empty", "size", "member_count = 0", "No members — often left behind after a project");

    public static IReadOnlyList<BrowseFilter> Filters =>
        [Security, Microsoft365, Distribution, Assigned, Dynamic, CloudOnly, Synced, RoleAssignable, Empty];

    public static IReadOnlyList<BrowseSort> Sorts =>
    [
        new("name", "Name", "display_name"),
        new("members", "Members", "member_count"),
        new("kind", "Type", "kind"),
    ];

    public static BrowseSort DefaultSort => Sorts[0];

    public static IReadOnlyList<string> TextColumns => ["display_name", "mail", "description"];
}
