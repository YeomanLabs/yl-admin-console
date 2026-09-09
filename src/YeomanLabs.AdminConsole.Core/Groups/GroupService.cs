using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Groups;

/// <summary>
/// Group page data access (UX §4.5, doc 04 §Groups). The cache is authoritative for member counts; the member list is
/// fetched on demand and paged. Membership writes come in M5.4.
/// </summary>
public sealed partial class GroupService
{
    private const string GroupId = "group";
    private const string OwnersId = "owners";
    private const int PageSize = 999;

    private readonly GraphClient _graph;
    private readonly CacheDb _db;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;
    private readonly ILogger<GroupService> _logger;

    public GroupService(GraphClient graph, CacheDb db, ActionLog actions, TenantContext tenant, ILogger<GroupService> logger)
    {
        _graph = graph;
        _db = db;
        _actions = actions;
        _tenant = tenant;
        _logger = logger;
    }

    public DirectoryGroup? GetCached(string id) => _db.GetGroup(id);

    /// <summary>Cached member ids and types; the page resolves user names from the cache before going to Graph.</summary>
    public IReadOnlyList<(string MemberId, string MemberType)> GetCachedMemberIds(string groupId) => _db.GetGroupMemberIds(groupId);

    public IReadOnlyList<DirectoryUser> GetCachedUsers(IEnumerable<string> ids) => _db.GetUsers(ids);

    public async Task<GroupLiveDetail> HydrateAsync(string id, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var responses = await _graph.BatchAsync(
        [
            BatchRequest.Get(GroupId, $"/groups/{id}?$select={GraphSelects.Group}"),
            BatchRequest.Get(OwnersId, $"/groups/{id}/owners?$select={GraphSelects.PersonRef}&$top=100"),
        ], ct).ConfigureAwait(false);

        var group = Section(responses[GroupId], body => body is { } b ? DirectoryGroup.FromJson(b) : throw new JsonException("empty group"));
        var owners = Section<IReadOnlyList<PersonRef>>(responses[OwnersId], body => Items(body).Select(PersonRef.FromJson).ToList());

        if (group.Value is { } fresh)
        {
            _db.Write(w => w.UpsertGroup(fresh));
        }

        var detail = new GroupLiveDetail
        {
            GroupId = id,
            Group = group,
            Owners = owners,
            ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
        };
        _logger.LogInformation("Hydrated group {GroupId} in {Ms:F0} ms", id, detail.ElapsedMilliseconds);
        return detail;
    }

    /// <summary>One page of members. Pass a previous page's <see cref="MemberPage.NextLink"/> to continue.</summary>
    public async Task<MemberPage> GetMembersAsync(string groupId, string? nextLink, CancellationToken ct)
    {
        var url = nextLink ?? $"groups/{groupId}/members?$select={GraphSelects.PersonRef}&$top={PageSize}";
        var page = await _graph.GetPageAsync(url, ct).ConfigureAwait(false);
        return new MemberPage
        {
            Members = page.Items.Select(PersonRef.FromJson).ToList(),
            NextLink = page.NextLink,
        };
    }

    private static SectionResult<T> Section<T>(BatchResponse response, Func<JsonElement?, T> read)
    {
        if (!response.IsSuccess) return SectionResult<T>.Failed(GraphException.FromBatchResponse(response.Status, response.Body, response.Headers));
        try
        {
            return SectionResult<T>.Ok(read(response.Body));
        }
        catch (JsonException ex)
        {
            return SectionResult<T>.Failed(new GraphException(response.Status, "ParseError", ex.Message, rawBody: response.Body?.GetRawText(), inner: ex));
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement? body) =>
        body is { } b && b.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}
