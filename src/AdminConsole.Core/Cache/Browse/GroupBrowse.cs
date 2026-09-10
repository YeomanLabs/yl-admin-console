namespace AdminConsole.Core.Cache.Browse;

/// <summary>The filters and sorts a Groups list offers, chosen around what makes a group editable or not.</summary>
public static class GroupBrowse
{
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
