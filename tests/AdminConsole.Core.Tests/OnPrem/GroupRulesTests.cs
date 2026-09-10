using AdminConsole.Core.OnPrem;
using FluentAssertions;
using Xunit;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>
/// The scope, category and nesting rules Active Directory enforces on groups (M12.5).
/// </summary>
/// <remarks>
/// Every one of these is a constraint the domain controller already has. The value of checking them first is not
/// safety — AD would refuse anyway — it is that AD refuses all of them with the same sentence. "The server is
/// unwilling to perform" is the same answer for a conversion that is impossible, one that is blocked by a
/// membership, and one that is blocked by a member, and an admin cannot act on any of them.
/// </remarks>
public sealed class GroupRulesTests
{
    private static OnPremGroup Group(GroupScope scope = GroupScope.Global, GroupCategory category = GroupCategory.Security,
        bool critical = false, string name = "Lab-Staff") => new()
    {
        ObjectGuid = Guid.NewGuid(),
        DistinguishedName = $"CN={name},OU=Groups,DC=corp,DC=example,DC=com",
        SamAccountName = name,
        Scope = scope,
        Category = category,
        IsCritical = critical,
    };

    // ---- scope conversions ------------------------------------------------------------------------------------

    [Fact]
    public void Global_to_domain_local_is_impossible_and_says_which_way_round_to_go()
    {
        // Not a permission problem and not a state problem: AD has never allowed this conversion directly. The
        // useful answer is the route, not the refusal.
        var refusal = GroupRules.ScopeChange(GroupScope.Global, GroupScope.DomainLocal, false, false, false);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("universal first");
    }

    [Fact]
    public void Domain_local_to_global_is_impossible_the_same_way()
    {
        GroupRules.ScopeChange(GroupScope.DomainLocal, GroupScope.Global, false, false, false)
            .Should().Contain("universal first");
    }

    [Fact]
    public void Global_to_universal_is_blocked_only_by_being_inside_a_global_group()
    {
        // A universal group may not be a member of a global one, so the membership has to go first.
        GroupRules.ScopeChange(GroupScope.Global, GroupScope.Universal, isMemberOfGlobalGroup: true, false, false)
            .Should().Contain("member of a global group");

        GroupRules.ScopeChange(GroupScope.Global, GroupScope.Universal, isMemberOfGlobalGroup: false, false, false)
            .Should().BeNull();
    }

    [Fact]
    public void Domain_local_to_universal_is_blocked_only_by_holding_a_domain_local_group()
    {
        GroupRules.ScopeChange(GroupScope.DomainLocal, GroupScope.Universal, false, containsDomainLocalGroup: true, false)
            .Should().Contain("contains a domain local group");

        GroupRules.ScopeChange(GroupScope.DomainLocal, GroupScope.Universal, false, containsDomainLocalGroup: false, false)
            .Should().BeNull();
    }

    [Fact]
    public void Universal_to_global_is_blocked_only_by_holding_a_universal_group()
    {
        GroupRules.ScopeChange(GroupScope.Universal, GroupScope.Global, false, false, containsUniversalGroup: true)
            .Should().Contain("contains a universal group");

        GroupRules.ScopeChange(GroupScope.Universal, GroupScope.Global, false, false, containsUniversalGroup: false)
            .Should().BeNull();
    }

    [Fact]
    public void Universal_to_domain_local_is_always_allowed()
    {
        // The one conversion with no conditions on it: domain local accepts everything universal could hold.
        GroupRules.ScopeChange(GroupScope.Universal, GroupScope.DomainLocal, true, true, true).Should().BeNull();
    }

    [Theory]
    [InlineData(GroupScope.Global)]
    [InlineData(GroupScope.DomainLocal)]
    [InlineData(GroupScope.Universal)]
    public void Converting_to_the_scope_it_already_has_is_never_refused(GroupScope scope)
    {
        GroupRules.ScopeChange(scope, scope, true, true, true).Should().BeNull();
    }

    // ---- nesting ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_global_group_can_only_contain_global_groups()
    {
        GroupRules.Nesting(GroupScope.Global, OnPremMemberKind.Group, GroupScope.DomainLocal).Should().NotBeNull();
        GroupRules.Nesting(GroupScope.Global, OnPremMemberKind.Group, GroupScope.Universal).Should().NotBeNull();
        GroupRules.Nesting(GroupScope.Global, OnPremMemberKind.Group, GroupScope.Global).Should().BeNull();
    }

    [Fact]
    public void A_universal_group_cannot_contain_a_domain_local_one()
    {
        GroupRules.Nesting(GroupScope.Universal, OnPremMemberKind.Group, GroupScope.DomainLocal)
            .Should().Contain("end of the chain");
        GroupRules.Nesting(GroupScope.Universal, OnPremMemberKind.Group, GroupScope.Universal).Should().BeNull();
    }

    [Fact]
    public void A_domain_local_group_can_contain_any_group()
    {
        // The classic resource group: everything ends up here, which is the point of it.
        GroupRules.Nesting(GroupScope.DomainLocal, OnPremMemberKind.Group, GroupScope.Global).Should().BeNull();
        GroupRules.Nesting(GroupScope.DomainLocal, OnPremMemberKind.Group, GroupScope.Universal).Should().BeNull();
        GroupRules.Nesting(GroupScope.DomainLocal, OnPremMemberKind.Group, GroupScope.DomainLocal).Should().BeNull();
    }

    [Theory]
    [InlineData(GroupScope.Global)]
    [InlineData(GroupScope.DomainLocal)]
    [InlineData(GroupScope.Universal)]
    public void People_and_computers_go_in_any_scope(GroupScope scope)
    {
        // The scope rules are about groups inside groups. A user is never the thing that is refused.
        GroupRules.Nesting(scope, OnPremMemberKind.User, null).Should().BeNull();
        GroupRules.Nesting(scope, OnPremMemberKind.Computer, null).Should().BeNull();
    }

    // ---- protected objects ------------------------------------------------------------------------------------

    [Fact]
    public void A_builtin_group_cannot_be_converted_and_is_named_in_the_reason()
    {
        var refusal = GroupRules.Protected(Group(critical: true, name: "Domain Admins"));

        refusal.Should().Contain("Domain Admins");
        refusal.Should().Contain("built-in");
    }

    [Fact]
    public void An_ordinary_group_is_not_protected()
    {
        GroupRules.Protected(Group()).Should().BeNull();
    }

    // ---- category ---------------------------------------------------------------------------------------------

    [Fact]
    public void Turning_a_security_group_into_a_distribution_one_warns_about_the_access_it_takes_away()
    {
        // It does not fail and AD says nothing. The people who notice are the ones who lost access, days later.
        var warning = GroupRules.CategoryWarning(GroupCategory.Security, GroupCategory.Distribution, 12);

        warning.Should().Contain("grants no access");
        warning.Should().Contain("all 12 members");
    }

    [Fact]
    public void The_warning_counts_one_member_in_words_that_read_properly()
    {
        GroupRules.CategoryWarning(GroupCategory.Security, GroupCategory.Distribution, 1)
            .Should().Contain("its one member");
    }

    [Fact]
    public void Going_the_other_way_takes_nothing_away_so_it_does_not_warn()
    {
        GroupRules.CategoryWarning(GroupCategory.Distribution, GroupCategory.Security, 12).Should().BeNull();
        GroupRules.CategoryWarning(GroupCategory.Security, GroupCategory.Security, 12).Should().BeNull();
    }

    // ---- cycles -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_group_inside_itself_is_warned_about_and_not_refused()
    {
        // Tried against a real DC: accepted without complaint, as were two- and three-group loops. The widely
        // repeated claim that AD prevents circular nesting is not what AD does, so refusing here would make the
        // console stricter than the directory — blocking work that is permitted, for a rule that is ours.
        var group = Group();

        GroupRules.CycleWarning(group, group.ObjectGuid, group.DistinguishedName)
            .Should().Contain("member of itself");
    }

    [Fact]
    public void Adding_a_group_that_already_contains_this_one_warns_about_the_loop()
    {
        var parentDn = "CN=Lab-Parent,OU=Groups,DC=corp,DC=example,DC=com";
        var child = Group(name: "Lab-Child") with { MemberOfDns = [parentDn] };

        var warning = GroupRules.CycleWarning(child, Guid.NewGuid(), parentDn);

        warning.Should().Contain("Lab-Parent");
        warning.Should().Contain("loop");
    }

    [Fact]
    public void Ordinary_nesting_says_nothing()
    {
        var group = Group() with { MemberOfDns = ["CN=Somewhere,OU=Groups,DC=corp,DC=example,DC=com"] };

        GroupRules.CycleWarning(group, Guid.NewGuid(), "CN=Unrelated,OU=Groups,DC=corp,DC=example,DC=com")
            .Should().BeNull();
    }

}
