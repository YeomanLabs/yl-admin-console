using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Core;

/// <summary>
/// Reads and writes <c>branding.json</c> beside the cache (M11.3). Absent means the neutral default, which is the
/// state every fresh installation starts in.
/// </summary>
public sealed class BrandingStore
{
    private readonly string _path;
    private readonly ILogger<BrandingStore> _logger;

    public BrandingStore(string path, ILogger<BrandingStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public static string DefaultPath => System.IO.Path.Combine(AppPaths.Root, "branding.json");

    /// <summary>Where this installation's branding is read from, so Settings can point at it.</summary>
    public string FilePath => _path;

    public Branding Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Branding();
            var read = JsonSerializer.Deserialize<Branding>(File.ReadAllText(_path), JsonFiles.Options);
            return (read ?? new Branding()).Sanitised();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A hand-edited file with a stray comma must not stop the app starting, or leave it unpaintable.
            _logger.LogWarning(ex, "Could not read {Path}; using the default branding", _path);
            return new Branding();
        }
    }

    public void Save(Branding branding)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(branding.Sanitised(), JsonFiles.Options));
            _logger.LogInformation("Branding saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {Path}", _path);
        }
    }

    /// <summary>Back to the neutral default.</summary>
    public void Reset()
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
