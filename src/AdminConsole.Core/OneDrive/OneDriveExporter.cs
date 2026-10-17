using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.OneDrive;

/// <summary>
/// Exports a OneDrive to a ZIP (M16.4): every file streamed to disk, checked against OneDrive's own hash, then packed —
/// never held in memory, and resumable at the file: an interrupted export picks up where it stopped.
/// </summary>
/// <remarks>
/// <para>Files land first in <c>{zip}.parts\</c> under numbered names (so no OneDrive path can hit Windows' path rules),
/// with a manifest of what each one is. Only when every file is down and checked is the ZIP written, from those files, to
/// <c>{zip}.tmp</c> and moved into place — so a ZIP that exists is a complete one.</para>
/// <para>An admin can't read someone else's OneDrive just by holding <c>Files.Read.All</c>; SharePoint wants them to be a
/// site admin. If they are not, the export makes them one for its duration and takes them off afterwards, failure or
/// not — both in the action log under the export's name.</para>
/// </remarks>
public sealed class OneDriveExporter
{
    private const string ManifestName = "manifest.json";

    private readonly GraphClient _graph;
    private readonly OneDriveService _oneDrive;
    private readonly IAuthService _auth;
    private readonly HttpClient _http;
    private readonly TenantContext _tenant;
    private readonly ActionLog _actions;
    private readonly ILogger<OneDriveExporter> _logger;

    public OneDriveExporter(GraphClient graph, OneDriveService oneDrive, IAuthService auth, HttpClient http, TenantContext tenant, ActionLog actions, ILogger<OneDriveExporter> logger)
    {
        _graph = graph;
        _oneDrive = oneDrive;
        _auth = auth;
        _http = http;
        _tenant = tenant;
        _actions = actions;
        _logger = logger;
    }

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public static string PartsFolder(string zipPath) => zipPath + ".parts";

    public async Task<ExportResult> ExportAsync(OneDriveDetail od, string label, string zipPath, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(zipPath)) throw new IOException($"{zipPath} already exists; choose another name.");
        var me = _tenant.SignedInUpn;
        var granted = false;
        if (!od.Admins.Any(a => a.Is(me)))
        {
            progress?.Report(new ExportProgress("Opening the OneDrive…", 0, 0, 0, 0));
            var grant = await _oneDrive.SetAdminAsync(od, label, me, "you (for the export)", admin: true, ct).ConfigureAwait(false);
            if (!grant.Success) throw new InvalidOperationException($"The OneDrive could not be opened for export: {grant.FriendlyError}");
            granted = !grant.WasNoOp;
        }

        try
        {
            var driveId = await DriveIdAsync(od.UserId, ct).ConfigureAwait(false);
            var result = await ExportDriveAsync(driveId, zipPath, progress, ct).ConfigureAwait(false);

            _actions.Record(new ActionRecord
            {
                Actor = me,
                TenantId = _tenant.TenantId,
                TargetType = ObjectType.User,
                TargetId = od.UserId,
                TargetName = label,
                Action = "onedrive.export",
                RequestSummary = zipPath,
                Outcome = result.Complete ? ActionOutcome.Success : ActionOutcome.Failed,
                Message = result.Summary,
            });
            return result;
        }
        finally
        {
            if (granted)
            {
                // Always, and not on the caller's token: a cancelled export must still give the access back.
                var back = await _oneDrive.SetAdminAsync(od, label, me, "you (for the export)", admin: false, CancellationToken.None).ConfigureAwait(false);
                if (!back.Success) _logger.LogWarning("Export of {UserId}: could not remove temporary admin: {Error}", od.UserId, back.FriendlyError);
            }
        }
    }

    /// <summary>
    /// The export itself, for a drive the signed-in admin can already read: list, download and check, pack. What
    /// <see cref="ExportAsync"/> runs once it has opened the OneDrive; also how a gate exports a test site's library.
    /// </summary>
    public async Task<ExportResult> ExportDriveAsync(string driveId, string zipPath, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(zipPath)) throw new IOException($"{zipPath} already exists; choose another name.");
        progress?.Report(new ExportProgress("Listing files…", 0, 0, 0, 0));
        var items = await ListAsync(driveId, ct).ConfigureAwait(false);
        return await DownloadAndPackAsync(driveId, items, zipPath, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Access granted a moment ago takes a little while to reach Graph, so a 403 or 404 is retried first.</summary>
    private async Task<string> DriveIdAsync(string userId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var doc = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}/drive?$select=id", null, ct).ConfigureAwait(false);
                return doc?.RootElement.GetString("id") ?? throw new InvalidOperationException("Graph returned no drive id.");
            }
            catch (GraphException ex) when ((ex.StatusCode is 403 or 404) && attempt < 6)
            {
                await Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Every file in the drive with its path, from one delta walk (folders give the paths; deleted items are skipped).</summary>
    internal async Task<IReadOnlyList<ExportItem>> ListAsync(string driveId, CancellationToken ct)
    {
        var url = $"drives/{driveId}/root/delta?$select=id,name,size,file,folder,parentReference,deleted,lastModifiedDateTime";
        var files = new List<ExportItem>();
        while (url is not null)
        {
            var page = await _graph.GetPageAsync(url, ct).ConfigureAwait(false);
            foreach (var e in page.Items)
            {
                if (e.TryGetProperty("deleted", out _) || !e.TryGetProperty("file", out var file)) continue;
                var parent = e.TryGetProperty("parentReference", out var p) ? p.GetString("path") : null;
                files.Add(new ExportItem(
                    e.GetString("id")!,
                    PathOf(parent, e.GetString("name") ?? "unnamed"),
                    e.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0,
                    file.TryGetProperty("hashes", out var h) ? h.GetString("quickXorHash") : null,
                    DateTimeOffset.TryParse(e.GetString("lastModifiedDateTime"), out var m) ? m : null));
            }

            url = page.NextLink;
        }

        return [.. files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary><c>/drives/{id}/root:/Folder/Sub</c> + name → <c>Folder/Sub/name</c>.</summary>
    internal static string PathOf(string? parentPath, string name)
    {
        var folder = string.Empty;
        if (parentPath is not null)
        {
            var i = parentPath.IndexOf("root:", StringComparison.Ordinal);
            if (i >= 0) folder = Uri.UnescapeDataString(parentPath[(i + 5)..]).Trim('/');
        }

        return folder.Length == 0 ? name : $"{folder}/{name}";
    }

    private async Task<ExportResult> DownloadAndPackAsync(string driveId, IReadOnlyList<ExportItem> items, string zipPath, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var parts = PartsFolder(zipPath);
        Directory.CreateDirectory(parts);
        File.WriteAllText(Path.Combine(parts, ManifestName), JsonSerializer.Serialize(items));

        var total = items.Sum(i => i.Size);
        long done = 0;
        int resumed = 0, verified = 0, sizeOnly = 0;
        var failed = new List<string>();

        for (var n = 0; n < items.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[n];
            var staged = Path.Combine(parts, $"{n:D6}.bin");
            progress?.Report(new ExportProgress("Downloading", n, items.Count, done, total, item.Path));

            // Resume: a file already down and still matching is kept.
            if (File.Exists(staged) && new FileInfo(staged).Length == item.Size && await MatchesAsync(staged, item, ct).ConfigureAwait(false))
            {
                resumed++;
                if (item.Hash is null) sizeOnly++; else verified++;
                done += item.Size;
                continue;
            }

            var ok = false;
            for (var attempt = 1; attempt <= 2 && !ok; attempt++)
            {
                var hash = await DownloadAsync(driveId, item, staged, ct).ConfigureAwait(false);
                ok = new FileInfo(staged).Length == item.Size && (item.Hash is null || string.Equals(hash, item.Hash, StringComparison.Ordinal));
                if (!ok) _logger.LogWarning("Export: {Path} did not match on attempt {Attempt}", item.Path, attempt);
            }

            if (!ok)
            {
                failed.Add(item.Path);
                File.Delete(staged);
                continue;
            }

            if (item.Hash is null) sizeOnly++; else verified++;
            done += item.Size;
        }

        progress?.Report(new ExportProgress("Packing the ZIP", items.Count, items.Count, done, total));
        if (failed.Count == 0)
        {
            var temp = zipPath + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                for (var n = 0; n < items.Count; n++)
                {
                    var entry = zip.CreateEntry(items[n].Path, CompressionLevel.Fastest);
                    if (items[n].Modified is { } modified) entry.LastWriteTime = modified;
                    await using var target = entry.Open();
                    await using var source = File.OpenRead(Path.Combine(parts, $"{n:D6}.bin"));
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                }
            }

            File.Move(temp, zipPath);
            Directory.Delete(parts, recursive: true);
        }

        return new ExportResult { ZipPath = zipPath, Files = items.Count, Bytes = total, Resumed = resumed, Verified = verified, SizeOnly = sizeOnly, Failed = failed };
    }

    /// <summary>Streams one file's content to <paramref name="path"/>, hashing as it goes. Returns the QuickXorHash.</summary>
    private async Task<string> DownloadAsync(string driveId, ExportItem item, string path, CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync([Scopes.FilesReadAll], ct).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphClient.V1BaseAddress}drives/{driveId}/items/{item.Id}/content");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Graph answers with a redirect to a pre-authenticated download URL; HttpClient follows it and does not carry
            // the bearer token across.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 5)
            {
                await Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var hash = new QuickXorHash();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.Append(buffer.AsSpan(0, read));
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return hash.ToBase64();
        }
    }

    private static async Task<bool> MatchesAsync(string path, ExportItem item, CancellationToken ct)
    {
        if (item.Hash is null) return true;
        var hash = new QuickXorHash();
        await using var source = File.OpenRead(path);
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hash.Append(buffer.AsSpan(0, read));
        return string.Equals(hash.ToBase64(), item.Hash, StringComparison.Ordinal);
    }
}
