using Microsoft.Extensions.Logging;

namespace AdminConsole.Core;

/// <summary>Local on-disk layout from docs/03-ARCHITECTURE.md §Paths. Everything lives under %LOCALAPPDATA%.</summary>
public static class AppPaths
{
    /// <summary>
    /// Where this app kept its data before M11 renamed it. Kept only so an existing install can be moved forward; the
    /// app never reads or writes here after the migration.
    /// </summary>
    public static string LegacyRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YeomanLabs", "AdminConsole");

    /// <summary>
    /// Names a different data folder for this process: everything below — tenants, tokens, cache, logs — then lives
    /// there instead. For test runs that must not see this PC's real tenants, and for keeping a second, separate setup.
    /// </summary>
    public const string RootOverrideVariable = "ADMINCONSOLE_DATA_ROOT";

    public static bool IsRootOverridden { get; } = Environment.GetEnvironmentVariable(RootOverrideVariable) is { Length: > 0 };

    public static string Root { get; } = IsRootOverridden
        ? Path.GetFullPath(Environment.GetEnvironmentVariable(RootOverrideVariable)!)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdminConsole");

    public static string CacheDirectory => Path.Combine(Root, "cache");
    public static string MsalCacheFileName => "msal.cache";
    public static string TemplatesDirectory => Path.Combine(Root, "templates");
    public static string LogsDirectory => Path.Combine(Root, "logs");
    public static string ActionsDbPath => Path.Combine(Root, "actions.db");

    public static string CacheDbPath(string tenantId) => Path.Combine(CacheDirectory, $"{tenantId}.db");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(TemplatesDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <summary>
    /// Moves a pre-M11 installation's data to the new location, once, before anything opens a file under it.
    /// Call this only from application startup.
    /// </summary>
    /// <remarks>
    /// Without it the rename would look to an admin exactly like a factory reset: the action log of every write ever
    /// made, the saved templates and offboarding profiles, the sync cache, and the MSAL token cache — so they would be
    /// asked to sign in again and find their history gone, with nothing on screen to explain why.
    /// </remarks>
    /// <remarks>Never into an overridden folder: that would carry this PC's real data into a test run's.</remarks>
    public static bool MigrateLegacyRoot(ILogger? logger = null) => !IsRootOverridden && Migrate(LegacyRoot, Root, logger);

    /// <summary>
    /// The actual move, against paths the caller supplies.
    /// </summary>
    /// <remarks>
    /// Separated so it can be tested in a temporary directory. It exists because the first version of this was tested
    /// through <see cref="MigrateLegacyRoot()"/> itself, which moved the real profile's data on the machine running
    /// the suite — a unit test must never be able to touch a real installation.
    /// </remarks>
    internal static bool Migrate(string from, string to, ILogger? logger = null)
    {
        try
        {
            // Moving rather than copying means it cannot run twice and cannot leave two diverging copies. An existing
            // destination means the migration has already happened, so there is nothing to do and nothing to overwrite.
            if (Directory.Exists(to) || !Directory.Exists(from)) return false;

            var parent = Path.GetDirectoryName(to);
            if (parent is not null) Directory.CreateDirectory(parent);

            Directory.Move(from, to);
            logger?.LogInformation("Moved the previous installation's data from {Old} to {New}", from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting with an empty folder is survivable — it re-syncs and asks for sign-in. Losing the old folder is
            // not, so it is left exactly where it is and the reason is logged loudly.
            logger?.LogError(ex, "Could not move the previous installation's data from {Old} to {New}. It has been " +
                                 "left where it is; the app will start with an empty cache.", from, to);
            return false;
        }
    }
}
