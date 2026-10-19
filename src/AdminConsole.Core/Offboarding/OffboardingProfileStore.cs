using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Core.Offboarding;

/// <summary>
/// Offboarding templates on disk under <c>templates\offboarding</c> (doc 06 §Offboarding templates), shared by every
/// tenant on this machine: they name steps and options, never a tenant's objects. "Standard leaver" is built in and
/// always first; a file that will not parse is reported rather than swallowed, because a template that quietly
/// vanishes is how an admin ends up running the wrong set of steps.
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
                        errors.Add($"{Path.GetFileName(path)} — no template name");
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

        // By name, as the list shows them: by file name, "contractor-2.json" would sort before "contractor.json".
        return [profiles[0], .. profiles.Skip(1).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The template the offboard dialog opens with: the saved one marked default, else the built-in. Two marked by hand
    /// is not an error worth refusing to open over; the first in the list wins.
    /// </summary>
    public static OffboardingProfile DefaultOf(IReadOnlyList<OffboardingProfile> profiles) =>
        profiles.FirstOrDefault(p => p is { IsDefault: true, IsBuiltIn: false }) ?? profiles[0];

    /// <summary>
    /// Writes the template and returns it with the file name it was saved under. A template saved before keeps its file,
    /// so a rename does not leave the old one behind; a new one gets a file of its own, never another template's.
    /// </summary>
    public OffboardingProfile Save(OffboardingProfile profile)
    {
        Directory.CreateDirectory(_directory);
        var fileName = profile.FileName ?? UniqueFileName(Slug(profile.Name));
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonFiles.Options));
        _logger.LogInformation("Saved the offboarding profile {Name} to {File}", profile.Name, fileName);
        return profile with { FileName = fileName };
    }

    /// <summary>
    /// Makes <paramref name="chosen"/> the one the dialog opens with, clearing the mark from every other saved template.
    /// The built-in (or null) means none is marked. Returns the templates as they now are.
    /// </summary>
    public IReadOnlyList<OffboardingProfile> SetDefault(IReadOnlyList<OffboardingProfile> profiles, OffboardingProfile? chosen)
    {
        var result = new List<OffboardingProfile>(profiles.Count);
        foreach (var profile in profiles)
        {
            var wanted = !profile.IsBuiltIn && chosen is { IsBuiltIn: false } && profile.FileName == chosen.FileName;
            result.Add(profile.IsBuiltIn || profile.IsDefault == wanted ? profile : Save(profile with { IsDefault = wanted }));
        }

        return result;
    }

    /// <summary>
    /// Imports an offboarding template file: refused when it is the other kind of template or breaks the editor's rules
    /// (a stamp Restore could not undo, a placeholder that does not exist), renamed on a clash, never the default.
    /// </summary>
    public OffboardingProfile? Import(string json, IReadOnlyList<OffboardingProfile> existing, out string? error)
    {
        if (Templates.TemplateTransfer.KindOf(json) == Templates.TemplateFileKind.User)
        {
            error = "This is a user template. Import it on the New users tab.";
            return null;
        }

        OffboardingProfile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OffboardingProfile>(json, JsonFiles.Options);
        }
        catch (JsonException ex)
        {
            error = Explain(ex);
            return null;
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Name))
        {
            error = "no template name";
            return null;
        }

        var renamed = parsed with
        {
            Name = Templates.TemplateTransfer.UniqueName(parsed.Name, existing.Select(p => p.Name)),
            IsDefault = false,
            FileName = null,
        };

        if (renamed.Problems(existing) is { Count: > 0 } problems)
        {
            error = string.Join(" ", problems);
            return null;
        }

        error = null;
        return Save(renamed);
    }

    /// <summary>The file an export writes: the template as it sits on disk, without being anyone's default.</summary>
    public static string Export(OffboardingProfile profile) => JsonSerializer.Serialize(profile with { IsDefault = false }, JsonFiles.Options);

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
                           && ex.Message.Contains("name", StringComparison.OrdinalIgnoreCase) => "no template name",
        JsonException => "not valid JSON",
        _ => ex.Message,
    };

    private string UniqueFileName(string slug)
    {
        var fileName = $"{slug}.json";
        for (var n = 2; File.Exists(Path.Combine(_directory, fileName)); n++) fileName = $"{slug}-{n}.json";
        return fileName;
    }

    /// <summary>A file name from a display name; shared with user templates, which name their files the same way.</summary>
    public static string Slug(string name) => JsonFiles.Slug(name);
}
