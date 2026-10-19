using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.OnPrem;
using TenantProfile = AdminConsole.Core.Auth.TenantProfile;

namespace AdminConsole.Core.Tests.Auth;

/// <summary>Settings → Active Directory: what a typed domain must look like, and how a tenant's domain list changes.</summary>
public sealed class TenantDomainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yl-domains-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---- what may be typed ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("corp.example.com")]
    [InlineData("  ad.contoso-group.co.uk ")]
    [InlineData("adlab.test")]
    [InlineData("CORP")]
    public void DomainProblem_AcceptsADnsName(string domain)
    {
        TenantDomain.DomainProblem(domain).Should().BeNull();
    }

    [Theory]
    [InlineData("", "Type the domain's DNS name, such as corp.example.com.")]
    [InlineData("CORP\\ayeoman", "That looks like an account, not a domain. Type the domain's DNS name, such as corp.example.com.")]
    [InlineData("ayeoman@corp.example.com", "That looks like an account, not a domain. Type the domain's DNS name, such as corp.example.com.")]
    [InlineData("ldap://corp.example.com", "Type the domain's DNS name without ldap:// or any other prefix.")]
    [InlineData("corp..example.com", "A domain name has only letters, digits, hyphens and dots, such as corp.example.com.")]
    [InlineData("corp example.com", "A domain name has only letters, digits, hyphens and dots, such as corp.example.com.")]
    [InlineData("-corp.example.com", "A domain name has only letters, digits, hyphens and dots, such as corp.example.com.")]
    public void DomainProblem_SaysWhatIsWrongInWords(string domain, string expected)
    {
        TenantDomain.DomainProblem(domain).Should().Be(expected);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("dc01.corp.example.com", null)]
    [InlineData("192.168.56.10", null)]
    [InlineData("dc01:636", "Type the domain controller's name alone; the port follows from LDAPS being on or off.")]
    [InlineData("ldaps://dc01", "Type the domain controller's name without ldap:// or any other prefix.")]
    [InlineData("dc 01", "A server name has only letters, digits, hyphens and dots, such as dc01.corp.example.com.")]
    public void ServerProblem_IsOptional_ButMustBeAName(string server, string? expected)
    {
        TenantDomain.ServerProblem(server).Should().Be(expected);
    }

    [Theory]
    [InlineData("CORP", true)]
    [InlineData("corp.example.com", false)]
    [InlineData("", false)]
    public void LooksLikeNetBiosName_IsAHintForANameWithNoDot(string domain, bool expected)
    {
        TenantDomain.LooksLikeNetBiosName(domain).Should().Be(expected);
    }

    // ---- how it binds -----------------------------------------------------------------------------------------------

    [Fact]
    public void ToOptions_BindsAsTheWindowsUser_WithNoCredential()
    {
        var options = new TenantDomain { Domain = " corp.example.com ", Server = "dc01", UseTls = true, Port = 636, DisabledOu = "OU=Leavers,DC=corp" }.ToOptions();

        options.Domain.Should().Be("corp.example.com");
        options.Server.Should().Be("dc01");
        options.UseTls.Should().BeTrue();
        options.Port.Should().Be(636);
        options.DisabledOu.Should().Be("OU=Leavers,DC=corp");
        options.Credential.Should().BeNull();
    }

    [Fact]
    public void ToOptions_KeepsAppsettingsLabCredential_OnlyForTheSameDomain()
    {
        // The lab case: appsettings names the domain with a credential; connecting that domain in Settings must not cut
        // the lab off. Any other domain still binds as the Windows user.
        var inherited = new LdapOptions { Domain = "adlab.test", Credential = new NetworkCredential("lab-admin", "x", "ADLAB"), Timeout = TimeSpan.FromSeconds(5) };

        var same = new TenantDomain { Domain = "ADLAB.test" }.ToOptions(inherited);
        var other = new TenantDomain { Domain = "corp.example.com" }.ToOptions(inherited);

        same.Credential!.UserName.Should().Be("lab-admin");
        same.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        other.Credential.Should().BeNull();
        other.Timeout.Should().Be(new LdapOptions().Timeout);
    }

    // ---- the tenant's list ------------------------------------------------------------------------------------------

    private static readonly TenantDomain Corp = new() { Domain = "corp.example.com" };
    private static readonly TenantDomain Emea = new() { Domain = "emea.example.com" };

    [Fact]
    public void WithDomain_AddsANewOneAtTheEnd()
    {
        var profile = new TenantProfile { Domains = [Corp] }.WithDomain(Emea);

        profile.Domains.Select(d => d.Domain).Should().Equal("corp.example.com", "emea.example.com");
    }

    [Fact]
    public void WithDomain_AnEditReplacesTheEntryInItsPlace()
    {
        var edited = Corp with { Server = "dc02.corp.example.com" };

        var profile = new TenantProfile { Domains = [Corp, Emea] }.WithDomain(edited, replacing: "corp.example.com");

        profile.Domains.Should().Equal(edited, Emea);
    }

    [Fact]
    public void WithDomain_TheSameNameAgain_ReplacesRatherThanListingItTwice()
    {
        var again = new TenantDomain { Domain = "CORP.example.com", UseTls = true };

        var profile = new TenantProfile { Domains = [Corp, Emea] }.WithDomain(again);

        profile.Domains.Should().Equal(again, Emea);
    }

    [Fact]
    public void WithDomain_RenamingOntoAnotherEntry_LeavesOneEntry()
    {
        var renamed = new TenantDomain { Domain = "emea.example.com", Server = "dc9" };

        var profile = new TenantProfile { Domains = [Corp, Emea] }.WithDomain(renamed, replacing: "corp.example.com");

        profile.Domains.Should().Equal(renamed);
    }

    [Fact]
    public void WithoutDomain_RemovesItWhateverItsCase()
    {
        new TenantProfile { Domains = [Corp, Emea] }.WithoutDomain("CORP.EXAMPLE.COM").Domains.Should().Equal(Emea);
    }

    [Fact]
    public void ADomainSavedFromSettings_RoundTripsThroughTenantJson()
    {
        var store = new TenantConfigStore(Path.Combine(_dir, "tenant.json"), new AuthOptions(), NullLogger<TenantConfigStore>.Instance);
        var profile = new TenantProfile { Name = "Contoso", ClientId = "22222222-2222-2222-2222-222222222222", TenantId = "t-1" }
            .WithDomain(new TenantDomain { Domain = "corp.example.com", Server = "dc01", UseTls = true, Port = 636, DisabledOu = "OU=Leavers,DC=corp,DC=example,DC=com" });

        store.SaveProfiles(new TenantProfileList().With(profile));

        store.LoadProfiles().Profiles.Single().Domains.Should().Equal(profile.Domains);
    }
}
