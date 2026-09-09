using System.Text.Json;

namespace YeomanLabs.AdminConsole.Core.Tests.Fakes;

/// <summary>Loads the scrubbed Graph payloads under tests/Fixtures (copied next to the test binaries).</summary>
public static class Fixtures
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Path_(string name) => Path.Combine(Root, name + ".json");

    public static string Text(string name) => File.ReadAllText(Path_(name));

    /// <summary>The whole collection document. Caller disposes.</summary>
    public static JsonDocument Document(string name) => JsonDocument.Parse(Text(name));

    /// <summary>Cloned <c>value[]</c> items so they outlive the document.</summary>
    public static IReadOnlyList<JsonElement> Items(string name)
    {
        using var doc = Document(name);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public static JsonElement Item(string name, int index) => Items(name)[index];

    /// <summary>Parses an inline JSON literal into a cloned element.</summary>
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
