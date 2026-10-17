using FluentAssertions;
using Xunit;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>M20.3: several domains behind one directory service.</summary>
public sealed class DirectoryRouterTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly FakeDirectoryService _corp = new("corp.contoso.com");
    private readonly FakeDirectoryService _emea = new("emea.contoso.com");
    private readonly DirectoryRouter _sut;

    public DirectoryRouterTests() => _sut = new DirectoryRouter([_corp, _emea]);

    [Fact]
    public async Task Status_IsConnected_WhenAnyDomainIs_AndNamesTheOneThatIsNot()
    {
        (await _sut.GetStatusAsync(Ct)).DomainName.Should().Be("corp.contoso.com, emea.contoso.com");

        _emea.IsConnected = false;
        var status = await _sut.GetStatusAsync(Ct);

        status.IsConnected.Should().BeTrue();
        status.DomainName.Should().Be("corp.contoso.com");
        status.Reason.Should().StartWith("Not reachable:");
    }

    [Fact]
    public async Task Search_MergesEveryDomain_AndAWriteGoesToTheObjectsOwnDomain()
    {
        _corp.AddUser("ada", displayName: "Ada Corp");
        var bea = _emea.AddUser("bea", displayName: "Bea Emea");

        var found = await _sut.SearchUsersAsync("a", 50, null, Ct);
        found.Select(u => u.DisplayName).Should().BeEquivalentTo(["Ada Corp", "Bea Emea"]);

        var outcome = await _sut.SetAttributesAsync(bea.ObjectGuid, new Dictionary<string, string?> { ["department"] = "Sales" }, Ct);

        outcome.Success.Should().BeTrue();
        (await _emea.GetUserAsync(bea.ObjectGuid, Ct))!.Department.Should().Be("Sales");
    }

    [Fact]
    public async Task AWriteToAnObjectNotYetRead_FindsItsDomainByAsking()
    {
        var bea = _emea.AddUser("bea");

        var outcome = await _sut.SetEnabledAsync(bea.ObjectGuid, false, Ct);

        outcome.Success.Should().BeTrue();
        (await _sut.SetEnabledAsync(Guid.NewGuid(), false, Ct)).FriendlyError.Should().Contain("not found in any configured domain");
    }

    [Fact]
    public async Task CreateUser_GoesToTheDomainTheOuIsIn()
    {
        var ou = _emea.AddOu("Staff");

        var created = await _sut.CreateUserAsync(new NewAdUser
        {
            ParentOuDn = ou.DistinguishedName, Name = "New Person", GivenName = "New", Surname = "Person",
            SamAccountName = "new.person", UserPrincipalName = "new.person@emea.contoso.com", Password = "Pa55word!Pa55word",
        }, Ct);

        created.Outcome.Success.Should().BeTrue();
        (await _emea.SearchUsersAsync("new.person", 5, null, Ct)).Should().ContainSingle();
        (await _corp.SearchUsersAsync("new.person", 5, null, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ACrossDomainMoveOrMembership_IsRefusedWithTheReason_NotAttempted()
    {
        var ada = _corp.AddUser("ada");
        var emeaOu = _emea.AddOu("Staff");
        var emeaGroup = _emea.AddGroup("emea-staff");

        (await _sut.MoveAsync(ada.ObjectGuid, emeaOu.DistinguishedName, Ct)).FriendlyError.Should().Contain("cross-domain move");
        (await _sut.AddToGroupAsync(emeaGroup.ObjectGuid, ada.ObjectGuid, Ct)).FriendlyError.Should().Contain("another domain");
    }

    [Fact]
    public async Task ImmutableId_IsLookedUpInEachDomain()
    {
        var bea = _emea.AddUser("bea");
        var anchor = Convert.ToBase64String(bea.ObjectGuid.ToByteArray());

        (await _sut.GetUserByImmutableIdAsync(anchor, Ct))!.ObjectGuid.Should().Be(bea.ObjectGuid);
    }
}
