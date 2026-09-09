namespace YeomanLabs.AdminConsole.Core;

/// <summary>Local on-disk layout from docs/03-ARCHITECTURE.md §Paths. Everything lives under %LOCALAPPDATA%.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YeomanLabs", "AdminConsole");

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
}
