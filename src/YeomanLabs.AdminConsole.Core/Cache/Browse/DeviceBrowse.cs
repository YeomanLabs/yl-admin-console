namespace YeomanLabs.AdminConsole.Core.Cache.Browse;

/// <summary>
/// The filters and sorts a Devices list offers. The columns named here are the aliases of the Entra/Intune union in
/// <c>CacheDb.Browse</c>, not raw table columns, because a device may exist in either directory or both.
/// </summary>
public static class DeviceBrowse
{
    public static readonly BrowseFilter Compliant =
        new("compliant", "Compliant", "compliance", "compliance = 'compliant'", "Meets its Intune compliance policies");

    public static readonly BrowseFilter NonCompliant =
        new("noncompliant", "Not compliant", "compliance", "compliance = 'noncompliant'", "Fails at least one compliance policy");

    public static readonly BrowseFilter GracePeriod =
        new("grace", "In grace period", "compliance", "compliance = 'inGracePeriod'", "Failing, but inside the window the policy allows");

    public static readonly BrowseFilter ComplianceUnknown =
        new("unknown", "Compliance unknown", "compliance",
            "(compliance IS NULL OR compliance IN ('unknown', 'conflict', 'error'))",
            "Never evaluated, or the policies disagree");

    public static readonly BrowseFilter Windows =
        new("windows", "Windows", "os", "os LIKE 'Windows%'", "");

    public static readonly BrowseFilter MacOs =
        new("macos", "macOS", "os", "(os LIKE 'macOS%' OR os LIKE 'Mac%')", "");

    public static readonly BrowseFilter IOs =
        new("ios", "iOS / iPadOS", "os", "(os LIKE 'iOS%' OR os LIKE 'iPad%')", "");

    public static readonly BrowseFilter Android =
        new("android", "Android", "os", "os LIKE 'Android%'", "");

    public static readonly BrowseFilter IntuneManaged =
        new("intune", "Intune-enrolled", "source", "source = 'intune'", "Enrolled in Intune, so it can be acted on");

    public static readonly BrowseFilter EntraOnly =
        new("entraonly", "Entra only", "source", "source = 'entra'",
            "Registered in Entra but never enrolled in Intune — no serial, no compliance, no remote actions");

    public static readonly BrowseFilter NotEncrypted =
        new("notencrypted", "Not encrypted", "encryption", "encrypted = 0", "Disk encryption is off or has not been reported");

    // 'now' is evaluated by SQLite, so the window is always relative to when the list is opened rather than to when
    // the app started. Both sides compare as ISO 8601 text, which sorts correctly date-first.
    public static readonly BrowseFilter Stale30 =
        new("stale30", "Quiet 30+ days", "activity",
            "last_activity < strftime('%Y-%m-%dT%H:%M:%S', 'now', '-30 days')",
            "No check-in or sign-in for a month — often a device nobody has any more");

    public static readonly BrowseFilter Stale90 =
        new("stale90", "Quiet 90+ days", "activity",
            "last_activity < strftime('%Y-%m-%dT%H:%M:%S', 'now', '-90 days')",
            "No check-in or sign-in for a quarter");

    public static readonly BrowseFilter Unassigned =
        new("unassigned", "No primary user", "owner",
            "(primary_user_id IS NULL OR primary_user_id = '')",
            "Nobody is recorded as the owner — shared, or lost track of");

    public static IReadOnlyList<BrowseFilter> Filters =>
    [
        Compliant, NonCompliant, GracePeriod, ComplianceUnknown,
        Windows, MacOs, IOs, Android,
        IntuneManaged, EntraOnly, NotEncrypted, Stale30, Stale90, Unassigned,
    ];

    public static IReadOnlyList<BrowseSort> Sorts =>
    [
        new("name", "Name", "device_name"),
        new("user", "Primary user", "primary_user_name"),
        new("activity", "Last seen", "last_activity"),
        new("os", "Operating system", "os"),
        new("compliance", "Compliance", "compliance"),
        new("enrolled", "Enrolled", "enrolled"),
    ];

    public static BrowseSort DefaultSort => Sorts[0];

    public static IReadOnlyList<string> TextColumns =>
        ["device_name", "serial", "model", "primary_user_name", "primary_user_upn"];
}
