using System.Text.Encodings.Web;
using System.Text.Json;

namespace YeomanLabs.AdminConsole.Core;

/// <summary>
/// Serializer settings for the JSON this app keeps on disk — offboarding profiles and user templates. These files are
/// meant to be opened and edited by hand, so they are indented, camelCase, and leave non-ASCII characters readable
/// rather than turning a name like "Åsa" into escape sequences. Separate from <c>GraphJson</c>, which is wire format.
/// </summary>
public static class JsonFiles
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// A file name from a display name: lower case, words joined by hyphens. Letters and digits survive, including
    /// non-ASCII ones, so a template called "Sälj — Stockholm" is still findable on disk by its name.
    /// </summary>
    public static string Slug(string name)
    {
        var slug = new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return slug.Length == 0 ? "untitled" : slug;
    }
}
