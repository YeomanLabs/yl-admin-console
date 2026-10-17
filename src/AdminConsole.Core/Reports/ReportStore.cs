using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdminConsole.Core.Reports;

/// <summary>
/// Reports as dated JSON files per tenant (M19): <c>%LOCALAPPDATA%\AdminConsole\reports\{tenant}\{kind}.json</c>. A report
/// is read from here when its page opens and rebuilt only when the admin asks, so a view never waits on a tenant-wide
/// walk and always says how old its figures are.
/// </summary>
public sealed class ReportStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;

    public ReportStore(string root) => _root = root;

    public static string DefaultRoot => Path.Combine(AppPaths.Root, "reports");

    public void Save<T>(string tenantId, string kind, T report)
    {
        var dir = Path.Combine(_root, tenantId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, kind + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(report, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The last saved report, or null when there is none or it cannot be read (an older shape, a damaged file).</summary>
    public T? Load<T>(string tenantId, string kind) where T : class
    {
        var path = Path.Combine(_root, tenantId, kind + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
