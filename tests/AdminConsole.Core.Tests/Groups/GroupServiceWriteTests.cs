using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Groups;
using AdminConsole.Core.Models;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Groups;

public sealed class GroupServiceWriteTests : IDisposable
{
    private const string GroupId = "g-1";
    private const string MemberId = "u-1";
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly GroupService _sut;

    public GroupServiceWriteTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new GroupService(graph, _db, _actions, _tenant, NullLogger<GroupService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    private void SeedGroup(bool dynamic = false, bool synced = false, bool roleAssignable = false, params string[] members)
    {
        _db.Write(w =>
        {
            w.UpsertGroup(new DirectoryGroup
            {
                Id = GroupId,
                DisplayName = "Sales",
                Kind = GroupKind.Security,
                IsDynamic = dynamic,
                OnPremisesSyncEnabled = synced,
                IsAssignableToRole = roleAssignable,
                Raw = "{}",
            });
            foreach (var m in members) w.AddGroupMember(GroupId, m, "user");
            w.RefreshMemberCount(GroupId);
        });
    }

    [Fact]
    public async Task AddMember_PostsRef_UpdatesTheCache_AndLogsWithInverse()
    {
        SeedGroup();
        _handler.Enqueue(HttpStatusCode.NoContent);

        var r = await _sut.AddMemberAsync(GroupId, MemberId, "Ada Lovelace", CancellationToken.None);

        r.Success.Should().BeTrue();
        r.Summary.Should().Be("Added Ada Lovelace to Sales");
        _handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/groups/g-1/members/$ref");
        _handler.Requests[0].Body.Should().Be("""{"@odata.id":"https://graph.microsoft.com/v1.0/directoryObjects/u-1"}""");
        _db.GetGroupMemberIds(GroupId).Should().ContainSingle().Which.MemberId.Should().Be(MemberId);
        _db.GetGroup(GroupId)!.MemberCount.Should().Be(1, "the member count drives the Access tab and the group page");

        var logged = _actions.Query(new ActionLogQuery { TargetId = GroupId }).Single();
        logged.Action.Should().Be("group.member.add");
        logged.TargetType.Should().Be(ObjectType.Group);
        logged.InverseAction.Should().Be("group.member.remove");
        logged.IsReversible.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveMember_DeletesRef_AndUndoAddsItBack()
    {
        SeedGroup(members: MemberId);
        _handler.Enqueue(HttpStatusCode.NoContent).Enqueue(HttpStatusCode.NoContent);

        var r = await _sut.RemoveMemberAsync(GroupId, MemberId, "Ada Lovelace", CancellationToken.None);

        r.Summary.Should().Be("Removed Ada Lovelace from Sales");
        _handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/groups/g-1/members/u-1/$ref");
        _db.GetGroupMemberIds(GroupId).Should().BeEmpty();
        _db.GetGroup(GroupId)!.MemberCount.Should().Be(0);

        var undone = await r.Undo!(CancellationToken.None);

        undone.Success.Should().BeTrue();
        undone.Summary.Should().Be("Added Ada Lovelace to Sales");
        _db.GetGroupMemberIds(GroupId).Should().ContainSingle();
        _db.GetGroup(GroupId)!.MemberCount.Should().Be(1);
        _actions.Get(r.ActionId)!.UndoneAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true, false, false, "Dynamic membership — members are computed from the rule")]
    [InlineData(false, true, false, "Mastered in on-prem AD (Phase 2)")]
    [InlineData(false, false, true, "Role-assignable group — needs Privileged Role Administrator")]
    public async Task SkipRules_AreEnforcedInTheService_NotJustTheUi(bool dynamic, bool synced, bool roleAssignable, string reason)
    {
        SeedGroup(dynamic, synced, roleAssignable);

        var r = await _sut.AddMemberAsync(GroupId, MemberId, "Ada Lovelace", CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be(reason);
        _handler.Requests.Should().BeEmpty("Graph would return 400/403; refusing locally is faster and clearer");
        _actions.Query(new ActionLogQuery { TargetId = GroupId }).Single().Outcome.Should().Be(ActionOutcome.Skipped);
        _db.GetGroupMemberIds(GroupId).Should().BeEmpty();
    }

    [Fact]
    public async Task RoleAssignableGroup_IsAllowedForAPrivilegedRoleAdministrator()
    {
        _tenant.Update(new TenantContextSnapshot
        {
            TenantId = "t-1",
            SignedInUpn = "admin@contoso.com",
            Roles = [DirectoryRoleRef.From(null, DirectoryRoleRef.PrivilegedRoleAdministrator)],
        });
        SeedGroup(roleAssignable: true);
        _handler.Enqueue(HttpStatusCode.NoContent);

        var r = await _sut.AddMemberAsync(GroupId, MemberId, "Ada Lovelace", CancellationToken.None);

        r.Success.Should().BeTrue();
        _handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task AddMember_GraphRejects_MapsTheErrorAndLeavesTheCacheAlone()
    {
        SeedGroup();
        _handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""");

        var r = await _sut.AddMemberAsync(GroupId, MemberId, "Ada Lovelace", CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("You don't have permission for this (Groups Administrator).");
        _db.GetGroupMemberIds(GroupId).Should().BeEmpty();
        _actions.Query(new ActionLogQuery { TargetId = GroupId }).Single().Outcome.Should().Be(ActionOutcome.Failed);
    }

    [Fact]
    public void BlockedReason_MatchesTheGroupsOwnRules()
    {
        var dynamicGroup = new DirectoryGroup { Id = "d", Kind = GroupKind.Security, IsDynamic = true, OnPremisesSyncEnabled = false, IsAssignableToRole = false, Raw = "{}" };
        var plain = new DirectoryGroup { Id = "p", Kind = GroupKind.Security, IsDynamic = false, OnPremisesSyncEnabled = false, IsAssignableToRole = false, Raw = "{}" };

        _sut.MembershipBlockedReason(dynamicGroup, false).Should().Be(DirectoryGroup.DynamicReason);
        _sut.MembershipBlockedReason(plain, false).Should().BeNull();
    }
}
