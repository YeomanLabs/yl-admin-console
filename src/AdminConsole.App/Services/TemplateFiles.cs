using System.IO;

namespace AdminConsole.App.Services;

/// <summary>Reading and writing template files for Templates → Import / Export, for both tabs.</summary>
public static class TemplateFiles
{
    public const string Filter = "Template files (*.json)|*.json|All files (*.*)|*.*";

    /// <summary>A template is a few kilobytes; anything far bigger is the wrong file, and is not read into memory.</summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>The file's text, or a reason it was not read.</summary>
    public static (string? Text, string? Problem) Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) return (null, "too large to be a template");
            return (File.ReadAllText(path), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Writes into a folder without replacing a file already there: "sales.json", then "sales-2.json".</summary>
    public static string WriteNew(string folder, string fileName, string text)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var path = Path.Combine(folder, fileName);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}-{n}{ext}");
        File.WriteAllText(path, text);
        return path;
    }
}
