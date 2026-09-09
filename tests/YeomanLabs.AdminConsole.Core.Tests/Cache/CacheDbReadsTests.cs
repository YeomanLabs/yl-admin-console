using FluentAssertions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Cache;

public sealed class CacheDbReadsTests : IDisposable
{
    private readonly CacheDb _db = CacheDb.OpenInMemory();

    public CacheDbReadsTests()
    {
        _db.Write(w =>
        {
            foreach (var u in Fixtures.Items("users").Select(DirectoryUser.FromJson)) w.UpsertUser(u);
            foreach (var g in Fixtures.Items("groups").Select(DirectoryGroup.FromJson)) w.UpsertGroup(g);
            foreach (var d in Fixtures.Items("devices").Select(EntraDevice.FromJson)) w.UpsertDevice(d);
            foreach (var m in Fixtures.Items("managed-devices").Select(ManagedDevice.FromJson)) w.UpsertManagedDevice(m);
            foreach (var s in Fixtures.Items("skus").Select(LicenseSku.FromJson)) w.UpsertSku(s);
            w.UpsertOrganization(Organization.FromJson(Fixtures.Item("organization", 0)));
        });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void GetUser_ParsesRawProjection_AndUnknownIdIsNull()
    {
        var first = Fixtures.Item("users", 0).GetString("id")!;

        var u = _db.GetUser(first)!;
        u.Id.Should().Be(first);
        u.UserPrincipalName.Should().EndWith("@contoso.com");
        _db.GetUser("nope").Should().BeNull();
        _db.GetUsers([first, "nope"]).Should().ContainSingle();
    }

    [Fact]
    public void Groups_ForMember_JoinThroughMemberships_WithCounts()
    {
        var groups = Fixtures.Items("groups").Select(DirectoryGroup.FromJson).ToList();
        _db.Write(w =>
        {
            w.AddGroupMember(groups[0].Id, "u-x", "user");
            w.AddGroupMember(groups[1].Id, "u-x", "user");
            w.AddGroupMember(groups[1].Id, "u-y", "user");
            w.RefreshMemberCount();
        });

        var forX = _db.GetGroupsForMember("u-x");
        forX.Select(g => g.Id).Should().BeEquivalentTo([groups[0].Id, groups[1].Id]);
        forX.Single(g => g.Id == groups[1].Id).MemberCount.Should().Be(2);
        _db.GetGroup(groups[1].Id)!.MemberCount.Should().Be(2);
        _db.GetGroupsForMember("nobody").Should().BeEmpty();
        _db.GetGroupMemberIds(groups[1].Id).Should().HaveCount(2);
    }

    [Fact]
    public void Devices_JoinBothWays_AndByPrimaryUser()
    {
        var md = ManagedDevice.FromJson(Fixtures.Item("managed-devices", 0));

        _db.GetManagedDevice(md.Id)!.DeviceName.Should().Be(md.DeviceName);
        _db.GetManagedDevicesForUser(md.PrimaryUserId!).Should().Contain(d => d.Id == md.Id);
        _db.GetManagedDeviceByAzureAdDeviceId(md.AzureAdDeviceId!)!.Id.Should().Be(md.Id);

        var entra = _db.GetEntraDeviceByDeviceId(md.AzureAdDeviceId!);
        entra.Should().NotBeNull("the captured Intune device has a matching Entra object");
        _db.GetEntraDevice(entra!.Id)!.DeviceId.Should().Be(md.AzureAdDeviceId);
        _db.GetManagedDevicesForUser("nobody").Should().BeEmpty();
    }

    [Fact]
    public void Skus_AndOrganization()
    {
        _db.GetSkus().Should().HaveCountGreaterThan(1).And.BeInAscendingOrder(s => s.SkuPartNumber);
        _db.GetOrganization()!.DisplayName.Should().Be("YeomanLabs Consulting");
    }
}
