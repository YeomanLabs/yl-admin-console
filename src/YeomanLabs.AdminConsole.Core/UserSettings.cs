using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace YeomanLabs.AdminConsole.Core;

/// <summary>How the app looks (UX §12 Appearance).</summary>
public enum AppTheme
{
    System,
    Dark,
    Light,
}

/// <summary>Row height across lists (UX §12 Appearance).</summary>
public enum AppDensity
{
    Comfortable,
    Compact,
}

/// <summary>
/// The choices an admin makes in Settings (UX §12), kept beside the cache rather than in appsettings.json: the config
/// file ships with the app, and a setting the app itself writes back into it would be lost on the next update.
/// </summary>
public sealed record UserSettings
{
    public int DeltaIntervalSeconds { get; init; } = 120;
    public int DeviceRefreshIntervalSeconds { get; init; } = 600;
    public AppTheme Theme { get; init; } = AppTheme.Dark;
    public AppDensity Density { get; init; } = AppDensity.Comfortable;

    /// <summary>The intervals an admin may choose. Anything faster than a minute just burns Graph quota.</summary>
    public static IReadOnlyList<int> DeltaChoices { get; } = [60, 120, 300, 600, 1800];

    public static IReadOnlyList<int> DeviceRefreshChoices { get; } = [300, 600, 1800, 3600];

    public static string Describe(int seconds) => seconds switch
    {
        < 60 => $"{seconds} seconds",
        60 => "1 minute",
        < 3600 => $"{seconds / 60} minutes",
        3600 => "1 hour",
        _ => $"{seconds / 3600} hours",
    };
}

/// <summary>Reads and writes <see cref="UserSettings"/> as one small JSON file. A broken file falls back to defaults.</summary>
public sealed class UserSettingsStore
{
    private readonly string _path;
    private readonly ILogger<UserSettingsStore> _logger;

    public UserSettingsStore(string path, ILogger<UserSettingsStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public static string DefaultPath => Path.Combine(AppPaths.Root, "settings.json");

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UserSettings();
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path), JsonFiles.Options) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Defaults are always usable, and losing a preference is not worth failing to start over.
            _logger.LogWarning(ex, "Could not read {Path}; using defaults", _path);
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonFiles.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {Path}", _path);
        }
    }
}
