using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.SharePoint;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.OneDrive;

/// <summary>
/// A user's OneDrive (M16, doc 14): read through Graph (where it is) and the SharePoint admin API (everything else), and
/// changed one setting at a time — lock state, external sharing, site admins — each read back before it is reported and
/// logged with its inverse. Two rules SharePoint enforces and the service says first: a locked OneDrive refuses every
/// other change, and no OneDrive may be more open than the tenant.
/// </summary>
public sealed class OneDriveService
{
    private readonly GraphClient _graph;
    private readonly SharePointClient _sharePoint;
    private readonly TenantContext _tenant;
    private readonly ActionLog _actions;
    private readonly ILogger<OneDriveService> _logger;
    private OneDriveSharing? _ceiling;

    public OneDriveService(GraphClient graph, SharePointClient sharePoint, TenantContext tenant, ActionLog actions, ILogger<OneDriveService> logger)
    {
        _graph = graph;
        _sharePoint = sharePoint;
        _tenant = tenant;
        _actions = actions;
        _logger = logger;
    }

    public bool IsAvailable => _tenant.IsLoaded && _tenant.HasScope(Scopes.SharePointFullControl);

    /// <summary>Waits between a write and its read-back; tests substitute an instant one.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    // ---- reads -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The OneDrive behind <paramref name="userId"/>, or null when it has not been created yet — SharePoint makes one the
    /// first time its owner opens OneDrive, and refuses to make one early for a delegated caller (doc 14 §1).
    /// </summary>
    public async Task<OneDriveDetail?> GetAsync(string userId, CancellationToken ct)
    {
        using var user = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}?$select=mySite", null, ct).ConfigureAwait(false);
        var mySite = user?.RootElement.GetString("mySite");
        if (string.IsNullOrWhiteSpace(mySite)) return null;
        return await ReadAsync(userId, mySite.TrimEnd('/'), ct).ConfigureAwait(false);
    }

    private async Task<OneDriveDetail> ReadAsync(string userId, string url, CancellationToken ct)
    {
        var hosts = SharePointHosts.FromOneDriveUrl(url) ?? SharePointHosts.FromVerifiedDomains(_tenant.VerifiedDomains)
            ?? throw new InvalidOperationException("This tenant's SharePoint address could not be worked out.");

        using var props = await _sharePoint.SendAsync(HttpMethod.Post, $"{hosts.Admin}/_api/SPO.Tenant/GetSitePropertiesByUrl",
            new { url, includeDetail = true }, "Read OneDrive settings", ct).ConfigureAwait(false)
            ?? throw new SharePointException(204, "Empty", "SharePoint returned no properties for this OneDrive.", "Read OneDrive settings");
        var siteId = props.RootElement.GetString("SiteId") ?? string.Empty;
        var admins = await ReadAdminsAsync(hosts, siteId, ct).ConfigureAwait(false);
        var ceiling = await CeilingAsync(hosts, ct).ConfigureAwait(false);
        return OneDriveDetail.FromJson(userId, url, hosts, props.RootElement, admins, ceiling);
    }

    private async Task<IReadOnlyList<SiteAdmin>> ReadAdminsAsync(SharePointHosts hosts, string siteId, CancellationToken ct)
    {
        using var doc = await _sharePoint.SendAsync(HttpMethod.Post, $"{hosts.Admin}/_api/SPO.Tenant/GetSiteAdministrators",
            new { siteId }, "Read site admins", ct).ConfigureAwait(false);
        return doc?.RootElement.TryGetProperty("value", out var v) == true && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(SiteAdmin.FromJson)]
            : [];
    }

    /// <summary>The tenant's OneDrive sharing setting, read once: no OneDrive may be more open (doc 14 §2).</summary>
    private async Task<OneDriveSharing> CeilingAsync(SharePointHosts hosts, CancellationToken ct)
    {
        if (_ceiling is { } known) return known;
        using var doc = await _sharePoint.SendAsync(HttpMethod.Get, $"{hosts.Admin}/_api/SPO.Tenant", null, "Read tenant sharing", ct).ConfigureAwait(false);
        var root = doc?.RootElement ?? default;
        var value = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("OneDriveSharingCapability", out _)
            ? OneDriveDetail.Long(root, "OneDriveSharingCapability")
            : root.ValueKind == JsonValueKind.Object ? OneDriveDetail.Long(root, "SharingCapability") : 2;
        _ceiling = (OneDriveSharing)(int)value;
        return _ceiling.Value;
    }

    // ---- writes ----------------------------------------------------------------------------------------------------

    /// <summary>Unlocked, read-only or no access. Undo puts back the previous state.</summary>
    public async Task<WriteOutcome> SetLockStateAsync(OneDriveDetail od, string label, string state, CancellationToken ct)
    {
        if (!OneDriveLock.All.Contains(state)) throw new ArgumentException($"Unknown lock state {state}.", nameof(state));
        var now = await ReadAsync(od.UserId, od.Url, ct).ConfigureAwait(false);
        if (string.Equals(now.LockState, state, StringComparison.OrdinalIgnoreCase)) return WriteOutcome.NoOp($"{label}'s OneDrive is already {OneDriveLock.Describe(state).ToLowerInvariant()}");

        var previous = now.LockState;
        return await WriteAsync(now, label, "onedrive.lock", $"{label}'s OneDrive is now {OneDriveLock.Describe(state).ToLowerInvariant()}",
            $"PATCH Sites('{now.SiteId}') LockState={state}",
            token => PatchSiteAsync(now, new Dictionary<string, object> { ["LockState"] = state }, token),
            after => string.Equals(after.LockState, state, StringComparison.OrdinalIgnoreCase),
            token => SetLockStateAsync(now, label, previous, token),
            JsonSerializer.Serialize(new { lockState = previous }), ct).ConfigureAwait(false);
    }

    /// <summary>Who the owner may share with. Refused while locked, and above the tenant's own setting.</summary>
    public async Task<WriteOutcome> SetSharingAsync(OneDriveDetail od, string label, OneDriveSharing sharing, CancellationToken ct)
    {
        var now = await ReadAsync(od.UserId, od.Url, ct).ConfigureAwait(false);
        var summary = $"Set {label}'s OneDrive sharing to {OneDriveSharingText.Describe(sharing).ToLowerInvariant()}";
        if (now.Sharing == sharing) return WriteOutcome.NoOp($"{label}'s OneDrive already allows {OneDriveSharingText.Describe(sharing).ToLowerInvariant()}");
        if (now.IsLocked) return Refused(now, label, "onedrive.sharing", summary, "The OneDrive is locked, and SharePoint refuses every other change while it is. Unlock it first.");
        if (OneDriveSharingText.Rank(sharing) > OneDriveSharingText.Rank(now.TenantCeiling))
        {
            return Refused(now, label, "onedrive.sharing", summary,
                $"The tenant allows at most \"{OneDriveSharingText.Describe(now.TenantCeiling)}\" for OneDrive; change that in the SharePoint admin centre first.");
        }

        var previous = now.Sharing;
        return await WriteAsync(now, label, "onedrive.sharing", summary,
            $"PATCH Sites('{now.SiteId}') SharingCapability={(int)sharing}",
            token => PatchSiteAsync(now, new Dictionary<string, object> { ["SharingCapability"] = (int)sharing }, token),
            after => after.Sharing == sharing,
            token => SetSharingAsync(now, label, previous, token),
            JsonSerializer.Serialize(new { sharing = (int)previous }), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes <paramref name="upn"/> a site collection admin of the OneDrive, or takes them off. The owner cannot be taken
    /// off — SharePoint would leave a OneDrive nobody administers.
    /// </summary>
    public async Task<WriteOutcome> SetAdminAsync(OneDriveDetail od, string label, string upn, string displayAs, bool admin, CancellationToken ct)
    {
        var now = await ReadAsync(od.UserId, od.Url, ct).ConfigureAwait(false);
        var summary = admin ? $"Made {displayAs} an admin of {label}'s OneDrive" : $"Took {displayAs} off {label}'s OneDrive admins";
        var has = now.Admins.Any(a => a.Is(upn));
        if (has == admin) return WriteOutcome.NoOp(admin ? $"{displayAs} already administers {label}'s OneDrive" : $"{displayAs} isn't an admin of {label}'s OneDrive");
        if (now.IsLocked) return Refused(now, label, "onedrive.admin", summary, "The OneDrive is locked, and SharePoint refuses every other change while it is. Unlock it first.");
        if (!admin && string.Equals(now.Owner, upn, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(now, label, "onedrive.admin", summary, $"{displayAs} owns this OneDrive, so they stay its admin.");
        }

        return await WriteAsync(now, label, admin ? "onedrive.admin.add" : "onedrive.admin.remove", summary,
            $"SetSiteAdmin {upn} {admin}",
            token => _sharePoint.SetSiteAdminAsync(now.Hosts, now.Url, SharePointClient.LoginNameOf(upn), admin, token),
            after => after.Admins.Any(a => a.Is(upn)) == admin,
            token => SetAdminAsync(now, label, upn, displayAs, !admin, token),
            JsonSerializer.Serialize(new { upn, admin = !admin }), ct).ConfigureAwait(false);
    }

    private async Task PatchSiteAsync(OneDriveDetail od, IReadOnlyDictionary<string, object> fields, CancellationToken ct)
    {
        using var _ = await _sharePoint.SendAsync(new HttpMethod("PATCH"),
            $"{od.Hosts.Admin}/_api/Microsoft.Online.SharePoint.TenantAdministration.Tenant/Sites('{od.SiteId}')", fields, "Change OneDrive settings", ct).ConfigureAwait(false);
    }

    private async Task<WriteOutcome> WriteAsync(OneDriveDetail od, string label, string action, string summary, string request,
        Func<CancellationToken, Task> write, Func<OneDriveDetail, bool> landed, Func<CancellationToken, Task<WriteOutcome>> undo,
        string inversePayload, CancellationToken ct)
    {
        try
        {
            await write(ct).ConfigureAwait(false);
        }
        catch (SharePointException ex)
        {
            var failedId = Record(od, label, action, request, ActionOutcome.Failed, SharePointException.Friendly(ex));
            return WriteOutcome.FailedSharePoint(summary, ex, failedId);
        }

        // SharePoint applies site changes as an operation; read back until it shows, briefly.
        var confirmed = false;
        foreach (var wait in new[] { 0.5, 1.5, 3, 5 })
        {
            await Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
            if (landed(await ReadAsync(od.UserId, od.Url, ct).ConfigureAwait(false)))
            {
                confirmed = true;
                break;
            }
        }

        var inverse = action switch
        {
            "onedrive.admin.add" => "onedrive.admin.remove",
            "onedrive.admin.remove" => "onedrive.admin.add",
            _ => action,
        };
        var id = Record(od, label, action, request, ActionOutcome.Success, summary, inverse, inversePayload);
        _logger.LogInformation("{Action} on OneDrive of {UserId} ({Confirmed})", action, od.UserId, confirmed ? "confirmed" : "not yet visible");

        return new WriteOutcome
        {
            Success = true,
            Summary = summary,
            ActionId = id,
            Warning = confirmed ? null : "SharePoint accepted the change but isn't showing it yet; it usually appears within a minute.",
            Undo = async token =>
            {
                var back = await undo(token).ConfigureAwait(false);
                if (back.Success) _actions.MarkUndone(id);
                return back;
            },
        };
    }

    private WriteOutcome Refused(OneDriveDetail od, string label, string action, string summary, string reason)
    {
        Record(od, label, action, summary, ActionOutcome.Skipped, reason);
        return new WriteOutcome { Success = false, Summary = summary, FriendlyError = reason };
    }

    private long Record(OneDriveDetail od, string label, string action, string request, ActionOutcome outcome, string message,
        string? inverseAction = null, string? inversePayload = null) =>
        _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = ObjectType.User,
            TargetId = od.UserId,
            TargetName = label,
            Action = action,
            RequestSummary = request,
            Outcome = outcome,
            Message = message,
            InverseAction = outcome == ActionOutcome.Success ? inverseAction : null,
            InversePayload = outcome == ActionOutcome.Success ? inversePayload : null,
        });
}
