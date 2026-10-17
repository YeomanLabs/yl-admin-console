namespace AdminConsole.Core.OneDrive;

/// <summary>A site's <c>LockState</c> as SharePoint spells it (M16).</summary>
public static class OneDriveLock
{
    public const string Unlock = "Unlock";
    public const string ReadOnly = "ReadOnly";
    public const string NoAccess = "NoAccess";

    public static readonly IReadOnlyList<string> All = [Unlock, ReadOnly, NoAccess];

    public static string Describe(string state) => state switch
    {
        ReadOnly => "Read-only — nobody can change anything in it",
        NoAccess => "No access — nobody can open it, its owner included",
        _ => "Unlocked",
    };

    public static bool IsLocked(string state) => !string.Equals(state, Unlock, StringComparison.OrdinalIgnoreCase);
}
