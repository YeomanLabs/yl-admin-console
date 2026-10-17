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

    /// <summary>The id of the profile made from a pre-M20 file or the shipped registration: stable across reads.</summary>
    public const string MigratedId = "default";

    /// <summary>
    /// What this installation should sign in with. The saved file wins; otherwise the shipped defaults; otherwise an
    /// unconfigured config, which is what sends the app to its setup screen rather than failing at MSAL.
    /// </summary>
    public TenantConfig Load()
    {
        var saved = LoadProfiles().Default;
        if (saved is not null && saved.IsConfigured) return saved.ToConfig();

        return new TenantConfig
        {
            ClientId = _shipped.ClientId,
            Authority = string.IsNullOrWhiteSpace(_shipped.Authority)
                ? new TenantConfig().Authority
                : _shipped.Authority,
        };
    }

    /// <summary>The setup screen's answer: it becomes (or replaces the registration of) the first profile.</summary>
    public void Save(TenantConfig config)
    {
        var list = LoadProfiles();
        var first = list.Profiles.FirstOrDefault() ?? new TenantProfile();
        SaveProfiles(list.With(first with
        {
            ClientId = config.ClientId.Trim(),
            Authority = config.Authority,
            TenantHint = config.TenantHint,
        }));
    }

    /// <summary>
    /// Every tenant profile (M20.1). A <c>tenant.json</c> from before M20 holds one registration as a bare object; it
    /// is read as a list of one, and written back in the new shape the next time anything is saved.
    /// </summary>
    public TenantProfileList LoadProfiles()
    {
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("profiles", out _))
                {
                    return JsonSerializer.Deserialize<TenantProfileList>(text, JsonFiles.Options) ?? new TenantProfileList();
                }

                var old = JsonSerializer.Deserialize<TenantConfig>(text, JsonFiles.Options);
                if (old is not null && old.IsConfigured)
                {
                    // A fixed id: this is read again before anything is saved, and a fresh id each time would make the
                    // save after sign-in add a second copy instead of updating the first.
                    var migrated = new TenantProfile { Id = MigratedId, ClientId = old.ClientId, Authority = old.Authority, TenantHint = old.TenantHint, Name = old.TenantHint ?? string.Empty };
                    return new TenantProfileList { Profiles = [migrated], LastUsedId = migrated.Id };
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file must not stop the app starting: fall through to the shipped default, or to setup.
            _logger.LogWarning(ex, "Could not read {Path}", _path);
        }

        if (TenantConfig.LooksLikeClientId(_shipped.ClientId))
        {
            var shipped = new TenantProfile
            {
                Id = MigratedId,
                ClientId = _shipped.ClientId,
                Authority = string.IsNullOrWhiteSpace(_shipped.Authority) ? new TenantConfig().Authority : _shipped.Authority,
            };
            return new TenantProfileList { Profiles = [shipped], LastUsedId = shipped.Id };
        }

        return new TenantProfileList();
    }

    public void SaveProfiles(TenantProfileList list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonFiles.Options));
            File.Move(temp, _path, overwrite: true);
            _logger.LogInformation("Tenant profiles saved ({Count})", list.Profiles.Count);
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
}
