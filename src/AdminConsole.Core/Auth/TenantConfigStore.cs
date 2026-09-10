using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Core.Auth;

/// <summary>
/// Reads and writes <c>tenant.json</c> beside the cache (M10.1). The shipped <c>appsettings.json</c> supplies the
/// fallback, so a build made for one tenant still works untouched while a client installation can point itself
/// somewhere else without editing anything under Program Files.
/// </summary>
public sealed class TenantConfigStore
{
    private readonly string _path;
    private readonly AuthOptions _shipped;
    private readonly ILogger<TenantConfigStore> _logger;

    public TenantConfigStore(string path, AuthOptions shipped, ILogger<TenantConfigStore> logger)
    {
        _path = path;
        _shipped = shipped;
        _logger = logger;
    }

    public static string DefaultPath => Path.Combine(AppPaths.Root, "tenant.json");

    /// <summary>
    /// What this installation should sign in with. The saved file wins; otherwise the shipped defaults; otherwise an
    /// unconfigured config, which is what sends the app to its setup screen rather than failing at MSAL.
    /// </summary>
    public TenantConfig Load()
    {
        var saved = ReadFile();
        if (saved is not null && saved.IsConfigured) return saved;

        return new TenantConfig
        {
            ClientId = _shipped.ClientId,
            Authority = string.IsNullOrWhiteSpace(_shipped.Authority)
                ? new TenantConfig().Authority
                : _shipped.Authority,
        };
    }

    public void Save(TenantConfig config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(config with { ClientId = config.ClientId.Trim() }, JsonFiles.Options));
            _logger.LogInformation("Tenant configuration saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {Path}", _path);
        }
    }

    /// <summary>Forgets this installation's registration, sending the app back to setup.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", _path);
        }
    }

    private TenantConfig? ReadFile()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<TenantConfig>(File.ReadAllText(_path), JsonFiles.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file must not stop the app starting: fall through to the shipped default, or to setup.
            _logger.LogWarning(ex, "Could not read {Path}", _path);
            return null;
        }
    }
}
