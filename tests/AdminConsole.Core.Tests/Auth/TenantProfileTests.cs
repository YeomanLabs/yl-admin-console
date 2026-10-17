using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;
using Xunit;
using AdminConsole.Core.Auth;
using TenantProfile = AdminConsole.Core.Auth.TenantProfile;

namespace AdminConsole.Core.Tests.Auth;

/// <summary>M20.1: <c>tenant.json</c> as a list of tenant profiles, and which cached account each one signs in with.</summary>
public sealed class TenantProfileTests : IDisposable
{
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yl-profiles-{Guid.NewGuid():N}");
    private readonly string _path;

    public TenantProfileTests() => _path = Path.Combine(_dir, "tenant.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TenantConfigStore Store() => new(_path, new AuthOptions(), NullLogger<TenantConfigStore>.Instance);

    [Fact]
    public void ASingleRegistrationFile_FromBeforeM20_IsReadAsAListOfOne()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, $$"""{ "clientId": "{{Client}}", "authority": "https://login.microsoftonline.com/organizations", "tenantHint": "contoso.onmicrosoft.com" }""");

        var list = Store().LoadProfiles();

        list.Profiles.Should().ContainSingle();
        list.Default!.ClientId.Should().Be(Client);
        list.Default.DisplayName.Should().Be("contoso.onmicrosoft.com");
        Store().Load().ClientId.Should().Be(Client, "the single-registration reader still answers");
        Store().LoadProfiles().Default!.Id.Should().Be(list.Default.Id, "read twice before a save, it is still the same profile");
    }

    [Fact]
    public void Profiles_RoundTrip_AndTheLastUsedOneIsTheDefault()
    {
        var store = Store();
        var a = new TenantProfile { Name = "Contoso", ClientId = Client, TenantId = "t-a" };
        var b = new TenantProfile { Name = "Fabrikam", ClientId = Client, TenantId = "t-b", Domains = [new TenantDomain { Domain = "corp.fabrikam.com" }] };
        store.SaveProfiles(new TenantProfileList { Profiles = [a, b], LastUsedId = b.Id });

        var back = store.LoadProfiles();

        back.Profiles.Select(p => p.Name).Should().Equal(["Contoso", "Fabrikam"]);
        back.Default!.Name.Should().Be("Fabrikam");
        back.Default.Domains.Single().Domain.Should().Be("corp.fabrikam.com");
        File.ReadAllText(_path).Should().NotContain("password", "a profile's domains never carry a credential");
    }

    [Fact]
    public void Setup_SavesIntoTheFirstProfile_WithoutDroppingTheOthers()
    {
        var store = Store();
        var a = new TenantProfile { Name = "Contoso", ClientId = Client, TenantId = "t-a" };
        var b = new TenantProfile { Name = "Fabrikam", ClientId = Client, TenantId = "t-b" };
        store.SaveProfiles(new TenantProfileList { Profiles = [a, b] });

        store.Save(new TenantConfig { ClientId = "33333333-3333-3333-3333-333333333333" });

        var back = store.LoadProfiles();
        back.Profiles.Should().HaveCount(2);
        back.Profiles[0].ClientId.Should().Be("33333333-3333-3333-3333-333333333333");
        back.Profiles[0].TenantId.Should().Be("t-a", "what the sign-in learnt about the tenant survives a registration change");
    }

    [Fact]
    public void ATenantId_PinsTheAuthority_SoAPartnerAccountGetsThisTenantsToken()
    {
        new TenantProfile { ClientId = Client }.EffectiveAuthority.Should().Be("https://login.microsoftonline.com/organizations");
        new TenantProfile { ClientId = Client, TenantId = "fabrikam.onmicrosoft.com" }.EffectiveAuthority
            .Should().Be("https://login.microsoftonline.com/fabrikam.onmicrosoft.com");
    }

    [Fact]
    public void List_WithAndWithout_ReplaceByIdAndForgetTheLastUsed()
    {
        var a = new TenantProfile { Name = "A" };
        var list = new TenantProfileList { Profiles = [a], LastUsedId = a.Id };

        list.With(a with { Name = "A2" }).Profiles.Single().Name.Should().Be("A2");
        list.Without(a.Id).Should().BeEquivalentTo(new TenantProfileList());
    }

    // ---- which account ------------------------------------------------------------------------------------------

    private static IAccount Account(string oid, string tenant) => new FakeAccount(oid + "." + tenant, tenant, oid + "@" + tenant);

    [Fact]
    public void ChooseAccount_PrefersTheProfilesOwnAccount_ThenOneHomedInTheTenant()
    {
        var mine = Account("u1", "t-a");
        var other = Account("u2", "t-b");

        AuthService.ChooseAccount([other, mine], mine.HomeAccountId.Identifier, "t-b", pickAccount: false).Should().BeSameAs(mine, "the remembered account wins");
        AuthService.ChooseAccount([mine, other], null, "t-b", pickAccount: false).Should().BeSameAs(other);
    }

    [Fact]
    public void ChooseAccount_ForANewTenant_AsksRatherThanGuessing_ButTheOldSingleTenantCaseStillTakesTheOnlyAccount()
    {
        var a = Account("u1", "t-a");

        AuthService.ChooseAccount([a], null, "fabrikam.onmicrosoft.com", pickAccount: true).Should().BeNull();
        AuthService.ChooseAccount([a], null, null, pickAccount: false).Should().BeSameAs(a);
    }

    private sealed class FakeAccount(string identifier, string tenantId, string username) : IAccount
    {
        public string Username { get; } = username;
        public string Environment => "login.microsoftonline.com";
        public AccountId HomeAccountId { get; } = new(identifier, identifier.Split('.')[0], tenantId);
    }
}
