using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Groups;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Groups;

public sealed class GroupServiceTests : IDisposable
{
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly GroupService _sut;

    public GroupServiceTests()
    {
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new GroupService(graph, _db, _actions, _tenant, NullLogger<GroupService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    [Fact]
    public async Task Hydrate_GroupAndOwners_WritesThrough_AndKeepsCachedMemberCount()
    {
        _db.Write(w =>
        {
            w.UpsertGroup(new DirectoryGroup { Id = "g1", DisplayName = "Old name", Kind = GroupKind.Security, IsDynamic = false, OnPremisesSyncEnabled = false, IsAssignableToRole = false, Raw = "{}" });
            w.AddGroupMember("g1", "u1", "user");
            w.AddGroupMember("g1", "u2", "user");
            w.RefreshMemberCount("g1");
        });

        _handler.Enqueue(HttpStatusCode.OK, Batch(
            ("group", 200, """{"id":"g1","displayName":"Sales","description":"Sellers","groupTypes":["Unified"],"mailEnabled":true,"securityEnabled":false,"mail":"sales@contoso.com"}"""),
            ("owners", 200, """{"value":[{"id":"o1","displayName":"Grace","userPrincipalName":"grace@contoso.com"}]}""")));

        var d = await _sut.HydrateAsync("g1", CancellationToken.None);

        d.Group.Value!.DisplayName.Should().Be("Sales");
        d.Group.Value.Kind.Should().Be(GroupKind.M365);
        d.Owners.Value.Should().ContainSingle().Which.Label.Should().Be("Grace");
        _sut.GetCached("g1")!.DisplayName.Should().Be("Sales", "write-through");
        _sut.GetCached("g1")!.MemberCount.Should().Be(2, "the upsert must not clobber the cached count");
        using var sent = JsonDocument.Parse(_handler.Requests[0].Body!);
        sent.RootElement.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("url").GetString()!).Should().Equal(
            "/groups/g1?$select=" + GraphSelects.Group,
            "/groups/g1/owners?$select=id,displayName,userPrincipalName&$top=100");
    }

    [Fact]
    public async Task Members_PageThroughNextLink()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Ada","userPrincipalName":"ada@contoso.com"},{"@odata.type":"#microsoft.graph.group","id":"g2","displayName":"Nested"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/groups/g1/members?$skiptoken=x"}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u2","displayName":"Grace","userPrincipalName":"grace@contoso.com"}]}""");

        var first = await _sut.GetMembersAsync("g1", null, CancellationToken.None);
        first.Members.Should().HaveCount(2);
        first.Members[0].IsUser.Should().BeTrue();
        first.Members[1].Kind.Should().Be("group");
        first.HasMore.Should().BeTrue();
        _handler.Requests[0].Uri.PathAndQuery.Should().StartWith("/v1.0/groups/g1/members?$select=id,displayName,userPrincipalName").And.Contain("$top=999");

        var second = await _sut.GetMembersAsync("g1", first.NextLink, CancellationToken.None);
        second.Members.Should().ContainSingle();
        second.HasMore.Should().BeFalse();
        _handler.Requests[1].Uri.Query.Should().Contain("skiptoken=x");
    }

    [Fact]
    public async Task Hydrate_OwnersForbidden_IsAFailedSectionOnly()
    {
        _handler.Enqueue(HttpStatusCode.OK, Batch(
            ("group", 200, """{"id":"g1","displayName":"Sales","groupTypes":[],"securityEnabled":true,"mailEnabled":false}"""),
            ("owners", 403, """{"error":{"code":"Authorization_RequestDenied","message":"nope"}}""")));

        var d = await _sut.HydrateAsync("g1", CancellationToken.None);

        d.Group.IsSuccess.Should().BeTrue();
        d.Owners.IsForbidden.Should().BeTrue();
    }

    private static string Batch(params (string Id, int Status, string Body)[] inner) =>
        "{\"responses\":[" + string.Join(",", inner.Select(i => $"{{\"id\":\"{i.Id}\",\"status\":{i.Status},\"body\":{i.Body}}}")) + "]}";
}
