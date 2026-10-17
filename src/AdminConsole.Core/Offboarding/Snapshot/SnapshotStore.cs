using System.Text.Encodings.Web;
using System.Text.Json;

namespace AdminConsole.Core.Offboarding.Snapshot;

/// <summary>
/// Snapshots on disk (M15.1): <c>%LOCALAPPDATA%\AdminConsole\snapshots\{tenant}\{user}_{yyyyMMdd-HHmmss}.json</c>.
/// Plain JSON beside the action log, so a snapshot is readable by a person with Notepad when the console is not there.
/// </summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;

    public SnapshotStore(string root) => _root = root;

    public static string DefaultRoot => Path.Combine(AppPaths.Root, "snapshots");

    public string Save(OffboardingSnapshot snapshot)
    {
        var dir = Path.Combine(_root, snapshot.TenantId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{snapshot.UserId}_{snapshot.TakenAt.UtcDateTime:yyyyMMdd-HHmmss}.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Json));
        // Written then moved, so a snapshot is never half there: a crash leaves either the whole file or none.
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Every snapshot of <paramref name="userId"/> in <paramref name="tenantId"/>, newest first.</summary>
    public IReadOnlyList<(string Path, OffboardingSnapshot Snapshot)> List(string tenantId, string userId)
    {
        var dir = Path.Combine(_root, tenantId);
        if (!Directory.Exists(dir)) return [];
        var found = new List<(string, OffboardingSnapshot)>();
        foreach (var file in Directory.EnumerateFiles(dir, $"{userId}_*.json"))
        {
            if (Load(file) is { } s) found.Add((file, s));
        }

        return [.. found.OrderByDescending(f => f.Item2.TakenAt)];
    }

    public static OffboardingSnapshot? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<OffboardingSnapshot>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
