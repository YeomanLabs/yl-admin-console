using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace YeomanLabs.AdminConsole.Core.Offboarding;

/// <summary>
/// Profiles on disk under <c>templates\offboarding</c> (doc 06 §Profiles). "Standard leaver" is built in and always
/// first; a file that will not parse is reported rather than swallowed, because a profile that quietly vanishes is
/// how an admin ends up running the wrong set of steps.
/// </summary>
public sealed class OffboardingProfileStore
{
    private readonly string _directory;
    private readonly ILogger<OffboardingProfileStore> _logger;

    public OffboardingProfileStore(string directory, ILogger<OffboardingProfileStore> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public static string DefaultDirectory => Path.Combine(AppPaths.TemplatesDirectory, "offboarding");

    /// <summary>Files that could not be read, so the dialog can say so instead of showing a shorter list.</summary>
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    /// <summary>The built-in default first, then the saved profiles by name.</summary>
    public IReadOnlyList<OffboardingProfile> Load(IEnumerable<IOffboardingStep> steps)
    {
        var profiles = new List<OffboardingProfile> { OffboardingProfile.StandardLeaver(steps) };
        var errors = new List<string>();

        if (Directory.Exists(_directory))
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var profile = JsonSerializer.Deserialize<OffboardingProfile>(json, JsonFiles.Options);
                    if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
                    {
                        errors.Add($"{Path.GetFileName(path)} — no profile name");
                        continue;
                    }

                    profiles.Add(profile with { FileName = Path.GetFileName(path) });
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not read the offboarding profile {Path}", path);
                    errors.Add($"{Path.GetFileName(path)} — {Explain(ex)}");
                }
            }
        }

        LoadErrors = errors;
        return profiles;
    }

    /// <summary>Writes the profile and returns it with the file name it was saved under.</summary>
    public OffboardingProfile Save(OffboardingProfile profile)
    {
        Directory.CreateDirectory(_directory);
        var fileName = profile.FileName ?? $"{Slug(profile.Name)}.json";
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonFiles.Options));
        _logger.LogInformation("Saved the offboarding profile {Name} to {File}", profile.Name, fileName);
        return profile with { FileName = fileName };
    }

    public void Delete(OffboardingProfile profile)
    {
        if (profile.FileName is null) return;
        var path = Path.Combine(_directory, profile.FileName);
        if (File.Exists(path)) File.Delete(path);
        _logger.LogInformation("Deleted the offboarding profile {Name}", profile.Name);
    }

    /// <summary>
    /// Turns a serializer message into something an admin can act on. System.Text.Json describes a missing required
    /// property by naming the CLR type, which tells the reader nothing about the file in front of them.
    /// </summary>
    private static string Explain(Exception ex) => ex switch
    {
        JsonException when ex.Message.Contains("missing required properties", StringComparison.OrdinalIgnoreCase)
                           && ex.Message.Contains("name", StringComparison.OrdinalIgnoreCase) => "no profile name",
        JsonException => "not valid JSON",
        _ => ex.Message,
    };

    /// <summary>A file name from a display name; shared with user templates, which name their files the same way.</summary>
    public static string Slug(string name) => JsonFiles.Slug(name);
}
