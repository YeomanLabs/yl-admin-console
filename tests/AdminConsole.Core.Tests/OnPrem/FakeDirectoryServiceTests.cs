using FluentAssertions;
using Xunit;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>
/// M12.1 — the in-memory directory every Phase 2 screen is developed against.
/// </summary>
/// <remarks>
/// These are as much a specification of what the LDAP implementation must also do as they are tests of the fake. The
/// fake is only useful if it refuses the same things a real domain controller refuses; one that says yes to
/// everything would teach the calling code to assume yes.
/// </remarks>
public sealed class FakeDirectoryServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task SearchingWithinAnOu_IncludesEverythingBeneathIt()
    {
        // An LDAP subtree search covers the base and its descendants, which is what an admin means by "in Sales".
        // Restricting to direct children would hide people the moment somebody adds a sub-OU.
        var ad = new FakeDirectoryService();
        var lab = ad.AddOu("Lab");
        var users = ad.AddOu("Users", lab.DistinguishedName);
        var sales = ad.AddOu("Sales", users.DistinguishedName);
        ad.AddUser("in.sales", sales.DistinguishedName);
        ad.AddUser("in.users", users.DistinguishedName);
        ad.AddUser("elsewhere", lab.DistinguishedName);

        var underUsers = await ad.SearchUsersAsync("", 50, users.DistinguishedName, Ct);

        underUsers.Select(u => u.SamAccountName).Should().BeEquivalentTo(["in.users", "in.sales"]);
        (await ad.SearchUsersAsync("", 50, sales.DistinguishedName, Ct)).Should().ContainSingle();
        (await ad.SearchUsersAsync("", 50, null, Ct)).Should().HaveCount(3, "no base means the whole domain");
    }

    private static (FakeDirectoryService Ad, OnPremOu Lab, OnPremOu Disabled) Lab()
    {
        var ad = new FakeDirectoryService();
        var lab = ad.AddOu("Lab");
        var disabled = ad.AddOu("Disabled", lab.DistinguishedName);
        return (ad, lab, disabled);
    }

    [Fact]
    public async Task NotConnected_IsAStateWithAReason_NotAnException()
    {
        // A laptop off the corporate network is the normal case, not a failure, and it must read as one.
        var ad = new FakeDirectoryService { IsConnected = false };

        var status = await ad.GetStatusAsync(Ct);

        status.IsConnected.Should().BeFalse();
        status.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Status_NamesTheDomainControllerItUsed()
    {
        // "Which DC" is the first question whenever a read looks stale, so it must always be on hand.
        var status = await new FakeDirectoryService().GetStatusAsync(Ct);

        status.DomainController.Should().NotBeNullOrWhiteSpace();
        status.RootDn.Should().Be("DC=adlab,DC=test");
    }

    [Fact]
    public async Task AUserIsFoundByTheGuidBehindEntrasImmutableId()
    {
        // This is the hybrid join, and it is the whole reason Phase 2 can act on a synced user without searching.
        var (ad, lab, _) = Lab();
        var user = ad.AddUser("ada", lab.DistinguishedName);
        var immutableId = Convert.ToBase64String(user.ObjectGuid.ToByteArray());

        var found = await ad.GetUserByImmutableIdAsync(immutableId, Ct);

        found!.ObjectGuid.Should().Be(user.ObjectGuid);
    }

    [Fact]
    public async Task ARubbishImmutableId_ReturnsNothingRatherThanThrowing()
    {
        var ad = new FakeDirectoryService();

        (await ad.GetUserByImmutableIdAsync("not-base64!", Ct)).Should().BeNull();
        (await ad.GetUserByImmutableIdAsync("dGhpcyBpcyBub3QgYSBndWlk", Ct)).Should().BeNull();
        (await ad.GetUserByImmutableIdAsync("", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task DisablingAUser_KeepsItsOtherFlags()
    {
        var (ad, lab, _) = Lab();
        var user = ad.AddUser("ada", lab.DistinguishedName);
        await ad.SetAttributesAsync(user.ObjectGuid, new Dictionary<string, string?> { ["title"] = "Engineer" }, Ct);

        var outcome = await ad.SetEnabledAsync(user.ObjectGuid, enabled: false, Ct);

        outcome.Success.Should().BeTrue();
        var after = await ad.GetUserAsync(user.ObjectGuid, Ct);
        after!.Enabled.Should().BeFalse();
        after.JobTitle.Should().Be("Engineer", "disabling is one bit, not a reset");
    }

    [Fact]
    public async Task DisablingSomethingAlreadyDisabled_IsANoOp_NotAFailure()
    {
        var (ad, lab, _) = Lab();
        var user = ad.AddUser("ada", lab.DistinguishedName, enabled: false);

        var outcome = await ad.SetEnabledAsync(user.ObjectGuid, enabled: false, Ct);

        outcome.Success.Should().BeTrue();
        outcome.WasNoOp.Should().BeTrue();
    }

    [Fact]
    public async Task AWriteSaysItIsAwaitingSync_BecauseTheCloudCopyWillBeStale()
    {
        // doc 01: "Hybrid admins should never wonder 'did that sync yet.'"
        var (ad, lab, _) = Lab();
        var user = ad.AddUser("ada", lab.DistinguishedName);

        var outcome = await ad.SetEnabledAsync(user.ObjectGuid, enabled: false, Ct);

        outcome.AwaitingSync.Should().BeTrue();
    }

    [Fact]
    public async Task ClearingAnAttribute_UsesNullRatherThanEmpty()
    {
        var (ad, lab, _) = Lab();
        var user = ad.AddUser("ada", lab.DistinguishedName);
        await ad.SetAttributesAsync(user.ObjectGuid, new Dictionary<string, string?> { ["department"] = "Sales" }, Ct);

        await ad.SetAttributesAsync(user.ObjectGuid, new Dictionary<string, string?> { ["department"] = "" }, Ct);

        (await ad.GetUserAsync(user.ObjectGuid, Ct))!.Department.Should().BeNull();
    }

    [Fact]
    public async Task MovingAUser_KeepsItsGuid_SoAnythingHoldingOneStaysValid()
    {
        var (ad, lab, disabled) = Lab();
        var user = ad.AddUser("leaver", lab.DistinguishedName);

        var outcome = await ad.MoveAsync(user.ObjectGuid, disabled.DistinguishedName, Ct);

        outcome.Success.Should().BeTrue();
        var after = await ad.GetUserAsync(user.ObjectGuid, Ct);
        after!.ObjectGuid.Should().Be(user.ObjectGuid, "the GUID is the identity; only the path moved");
        after.DistinguishedName.Should().EndWith(disabled.DistinguishedName);
        after.ParentOuDn.Should().Be(disabled.DistinguishedName);
    }

    [Fact]
    public async Task MovingAnOu_TakesItsChildrenWithIt()
    {
        var (ad, lab, _) = Lab();
        var sales = ad.AddOu("Sales", lab.DistinguishedName);
        var user = ad.AddUser("k.johnson", sales.DistinguishedName);
        var archive = ad.AddOu("Archive");

        await ad.MoveAsync(sales.ObjectGuid, archive.DistinguishedName, Ct);

        var moved = await ad.GetUserAsync(user.ObjectGuid, Ct);
        moved!.DistinguishedName.Should().EndWith($"OU=Sales,{archive.DistinguishedName}",
            "a child follows its container, and its GUID does not change");
    }

    [Fact]
    public async Task AnOuCannotBeMovedInsideItself()
    {
        var (ad, lab, _) = Lab();
        var child = ad.AddOu("Child", lab.DistinguishedName);

        var outcome = await ad.MoveAsync(lab.ObjectGuid, child.DistinguishedName, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("inside itself");
    }

    [Fact]
    public async Task ANonEmptyContainerCannotBeDeleted()
    {
        var (ad, lab, _) = Lab();
        ad.AddUser("ada", lab.DistinguishedName);

        var outcome = await ad.DeleteAsync(lab.ObjectGuid, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("Empty it");
    }

    [Fact]
    public async Task AProtectedOu_SaysWhichCheckboxToUntick()
    {
        var ad = new FakeDirectoryService();
        var ou = ad.AddOu("Payroll", protectedFromDeletion: true);

        var outcome = await ad.DeleteAsync(ou.ObjectGuid, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("protected from accidental deletion");
    }

    [Fact]
    public async Task TheGroupsScopeAndCategoryAreKept_BecauseEntraHasNoEquivalent()
    {
        var ad = new FakeDirectoryService();
        var local = ad.AddGroup("FileShare-RW", scope: GroupScope.DomainLocal);
        var dist = ad.AddGroup("Announce", scope: GroupScope.Universal, category: GroupCategory.Distribution);

        (await ad.GetGroupAsync(local.ObjectGuid, Ct))!.ScopeText.Should().Be("Domain local security group");
        (await ad.GetGroupAsync(dist.ObjectGuid, Ct))!.ScopeText.Should().Be("Universal distribution group");
    }

    [Fact]
    public async Task AGroupCanContainAGroup_AndItAppearsAsItself()
    {
        // Flattening here would hide the structure the admin is usually trying to see.
        var ad = new FakeDirectoryService();
        var all = ad.AddGroup("Lab-AllStaff", scope: GroupScope.Universal);
        var eng = ad.AddGroup("Lab-Engineering");
        var ada = ad.AddUser("ada");
        await ad.AddToGroupAsync(eng.ObjectGuid, ada.ObjectGuid, Ct);

        await ad.AddToGroupAsync(all.ObjectGuid, eng.ObjectGuid, Ct);

        var members = await ad.GetGroupMembersAsync(all.ObjectGuid, Ct);
        members.Should().ContainSingle();
        members[0].Kind.Should().Be(OnPremMemberKind.Group);
        members[0].Name.Should().Be("Lab-Engineering");
    }

    [Fact]
    public async Task AGroupInsideItself_IsAllowedAndWarnedAbout_BecauseThatIsWhatADDoes()
    {
        // This test used to assert a refusal, on the strength of the widely repeated claim that AD prevents circular
        // nesting. Tried against a real domain controller, every loop was accepted without complaint — a group inside
        // itself, a two-group loop, a three-group loop. The fake was enforcing a rule the thing it stands in for does
        // not have, which is the same failure as a fake that accepts everything, pointing the other way.
        var ad = new FakeDirectoryService();
        var g = ad.AddGroup("Lab-Engineering");

        var outcome = await ad.AddToGroupAsync(g.ObjectGuid, g.ObjectGuid, Ct);

        outcome.Success.Should().BeTrue();
        outcome.Warning.Should().Contain("member of itself");
    }

    [Fact]
    public async Task ATwoGroupLoop_IsAllowedAndWarnedAbout()
    {
        var ad = new FakeDirectoryService();
        var a = ad.AddGroup("Lab-A");
        var b = ad.AddGroup("Lab-B");

        (await ad.AddToGroupAsync(a.ObjectGuid, b.ObjectGuid, Ct)).Warning.Should().BeNull();

        var loop = await ad.AddToGroupAsync(b.ObjectGuid, a.ObjectGuid, Ct);

        loop.Success.Should().BeTrue();
        loop.Warning.Should().Contain("loop");
    }

    [Fact]
    public async Task AddingSomeoneTwice_IsANoOp()
    {
        var ad = new FakeDirectoryService();
        var g = ad.AddGroup("Lab-Engineering");
        var ada = ad.AddUser("ada");
        await ad.AddToGroupAsync(g.ObjectGuid, ada.ObjectGuid, Ct);

        var outcome = await ad.AddToGroupAsync(g.ObjectGuid, ada.ObjectGuid, Ct);

        outcome.Success.Should().BeTrue();
        outcome.WasNoOp.Should().BeTrue();
    }

    [Fact]
    public async Task RemovingSomeoneFromTheirPrimaryGroup_IsRefusedWithTheRealReason()
    {
        // AD accepts this write and changes nothing, which is the worst possible answer. Say why instead.
        var ad = new FakeDirectoryService();
        var domainUsers = ad.AddGroup("Domain Users", critical: true);
        var ada = ad.AddUser("ada");

        var outcome = await ad.RemoveFromGroupAsync(domainUsers.ObjectGuid, ada.ObjectGuid, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("primary group");
    }

    [Fact]
    public async Task ABuiltInGroupCannotBeDeleted()
    {
        var ad = new FakeDirectoryService();
        var g = ad.AddGroup("Domain Admins", critical: true);

        (await ad.DeleteAsync(g.ObjectGuid, Ct)).FriendlyError.Should().Contain("built-in");
    }

    [Fact]
    public async Task ARenameKeepsTheGuidAndMovesTheChildren()
    {
        var (ad, lab, _) = Lab();
        var user = ad.AddUser("ada", lab.DistinguishedName);

        await ad.RenameAsync(lab.ObjectGuid, "Laboratory", Ct);

        var moved = await ad.GetUserAsync(user.ObjectGuid, Ct);
        moved!.DistinguishedName.Should().Contain("OU=Laboratory");
        moved.ObjectGuid.Should().Be(user.ObjectGuid);
    }

    [Fact]
    public async Task TwoObjectsCannotShareANameInOneContainer()
    {
        var (ad, lab, disabled) = Lab();
        ad.AddUser("ada", disabled.DistinguishedName, displayName: "Ada Lovelace");
        var other = ad.AddUser("ada2", lab.DistinguishedName, displayName: "Ada Lovelace");

        var outcome = await ad.MoveAsync(other.ObjectGuid, disabled.DistinguishedName, Ct);

        outcome.Success.Should().BeFalse();
        outcome.ResultCode.Should().Be(DirectoryErrorText.EntryAlreadyExists);
    }

    [Fact]
    public async Task APasswordThatFailsPolicy_ComesBackWithTheReason_NotACode()
    {
        var ad = new FakeDirectoryService();
        var ada = ad.AddUser("ada");

        var outcome = await ad.SetPasswordAsync(ada.ObjectGuid, "short", mustChangeAtNextLogon: true, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("does not meet the domain's policy");
    }

    [Fact]
    public async Task ActingOnSomethingThatIsGone_SaysSo()
    {
        var ad = new FakeDirectoryService();

        var outcome = await ad.SetEnabledAsync(Guid.NewGuid(), enabled: false, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task SearchingFindsByNameOrSignInName_AndRespectsTheLimit()
    {
        var ad = new FakeDirectoryService();
        ad.AddUser("ada.lovelace", displayName: "Ada Lovelace");
        ad.AddUser("alan.turing", displayName: "Alan Turing");
        ad.AddUser("grace.hopper", displayName: "Grace Hopper");

        (await ad.SearchUsersAsync("lovelace", 10, null, Ct)).Should().ContainSingle();
        (await ad.SearchUsersAsync("a", 2, null, Ct)).Should().HaveCount(2, "the limit is honoured");
        (await ad.SearchUsersAsync("", 10, null, Ct)).Should().HaveCount(3, "empty means everyone");
    }
}
