using System.Text.Json;

namespace AdminConsole.Core.Templates;

/// <summary>
/// Pending hybrid creates on disk (M17.3): <c>%LOCALAPPDATA%\AdminConsole\pending\{tenant}.json</c>, written whole to a
/// temp file and moved into place, so a crash leaves the last good list rather than half of one.
/// </summary>
public sealed class PendingProvisioningStore
{
    private readonly string _directory;
    private readonly object _gate = new();

    public PendingProvisioningStore(string directory) => _directory = directory;

    public static string DefaultDirectory => Path.Combine(AppPaths.Root, "pending");

    public IReadOnlyList<PendingProvisioning> Load(string tenantId)
    {
        lock (_gate)
        {
            var path = PathFor(tenantId);
            if (!File.Exists(path)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<PendingProvisioning>>(File.ReadAllText(path), JsonFiles.Options) ?? [];
            }
            catch (JsonException)
            {
                // A damaged file is kept for a person to look at, and the list starts again rather than failing every sync.
                File.Move(path, path + $".damaged-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
                return [];
            }
        }
    }

    /// <summary>Adds or replaces <paramref name="item"/> by id.</summary>
    public void Save(PendingProvisioning item)
    {
        lock (_gate)
        {
            var all = Load(item.TenantId).Where(p => p.Id != item.Id).Append(item).OrderBy(p => p.CreatedAt).ToList();
            Directory.CreateDirectory(_directory);
            var path = PathFor(item.TenantId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, JsonFiles.Options));
            File.Move(temp, path, overwrite: true);
        }
    }

    private string PathFor(string tenantId) => Path.Combine(_directory, $"{tenantId}.json");
}
