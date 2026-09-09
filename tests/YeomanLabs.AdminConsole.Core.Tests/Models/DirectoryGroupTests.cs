using FluentAssertions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Models;

public sealed class DirectoryGroupTests
{
    [Fact]
    public void FromJson_ParsesEveryCapturedGroup()
    {
        var groups = Fixtures.Items("groups").Select(DirectoryGroup.FromJson).ToList();

        groups.Should().HaveCountGreaterThan(3);
        groups.Should().OnlyHaveUniqueItems(g => g.Id);
        groups.Should().OnlyContain(g => g.MemberCount == 0, "counts come from group_members, not the payload");
        groups.Should().Contain(g => g.Kind == GroupKind.Security);
    }

    [Fact]
    public void FromJson_DeltaPayload_CarriesMembersDelta()
    {
        var items = Fixtures.Items("groups-delta");
        var withMembers = items.Where(i => i.TryGetProperty("members@delta", out _)).ToList();

        withMembers.Should().NotBeEmpty();
        var entries = withMembers.SelectMany(g => g.GetProperty("members@delta").EnumerateArray()).ToList();
        entries.Should().Contain(m => m.IsRemoved(), "the captured delta contains membership removals");
        entries.Should().OnlyContain(m => m.GetString("@odata.type") != null && m.GetString("id") != null);
        items.Select(DirectoryGroup.FromJson).Should().HaveCount(items.Count);
    }

    [Theory]
    [InlineData("""["Unified"]""", true, true, GroupKind.M365)]
    [InlineData("""["Unified","DynamicMembership"]""", true, true, GroupKind.M365)]
    [InlineData("""[]""", false, true, GroupKind.Security)]
    [InlineData("""["DynamicMembership"]""", false, true, GroupKind.Security)]
    [InlineData("""[]""", true, true, GroupKind.DistributionOrMailSecurity)]
    [InlineData("""[]""", true, false, GroupKind.DistributionOrMailSecurity)]
    public void Kind_DerivedPerDoc05(string groupTypes, bool mailEnabled, bool securityEnabled, GroupKind expected)
    {
        var g = DirectoryGroup.FromJson(Fixtures.Parse($$"""{"id":"g","groupTypes":{{groupTypes}},"mailEnabled":{{mailEnabled.ToString().ToLowerInvariant()}},"securityEnabled":{{securityEnabled.ToString().ToLowerInvariant()}}}"""));

        g.Kind.Should().Be(expected);
        g.IsDynamic.Should().Be(groupTypes.Contains("DynamicMembership"));
    }

    [Fact]
    public void MembershipSkipRules_FollowDoc04()
    {
        var dynamic = DirectoryGroup.FromJson(Fixtures.Parse("""{"id":"d","groupTypes":["DynamicMembership"],"securityEnabled":true,"mailEnabled":false}"""));
        var synced = DirectoryGroup.FromJson(Fixtures.Parse("""{"id":"s","groupTypes":[],"securityEnabled":true,"mailEnabled":false,"onPremisesSyncEnabled":true}"""));
        var roleAssignable = DirectoryGroup.FromJson(Fixtures.Parse("""{"id":"r","groupTypes":[],"securityEnabled":true,"mailEnabled":false,"isAssignableToRole":true}"""));
        var plain = DirectoryGroup.FromJson(Fixtures.Parse("""{"id":"p","groupTypes":[],"securityEnabled":true,"mailEnabled":false}"""));

        dynamic.MembershipChangeBlockedReason().Should().Be(DirectoryGroup.DynamicReason);
        synced.MembershipChangeBlockedReason().Should().Be(DirectoryGroup.SyncedReason);
        roleAssignable.MembershipChangeBlockedReason().Should().Be(DirectoryGroup.RoleAssignableReason);
        roleAssignable.MembershipChangeBlockedReason(hasPrivilegedRoleAdmin: true).Should().BeNull();
        plain.MembershipChangeBlockedReason().Should().BeNull();

        dynamic.Badges.Should().Equal("Security", "Dynamic");
        synced.Badges.Should().Equal("Security", "Synced");
        roleAssignable.Badges.Should().Equal("Security", "Role-assignable");
    }
}
