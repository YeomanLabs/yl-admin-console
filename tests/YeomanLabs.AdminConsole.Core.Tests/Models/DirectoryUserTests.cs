using FluentAssertions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Models;

public sealed class DirectoryUserTests
{
    [Fact]
    public void FromJson_ParsesEveryCapturedUser()
    {
        var users = Fixtures.Items("users").Select(DirectoryUser.FromJson).ToList();

        users.Should().HaveCountGreaterThan(3);
        users.Should().OnlyHaveUniqueItems(u => u.Id);
        users.Should().OnlyContain(u => u.UserPrincipalName.EndsWith("@contoso.com"), "fixtures are scrubbed");
        users.Should().OnlyContain(u => u.Raw.Contains(u.Id));
        users.Select(u => u.IsDisabled).Should().Contain(true, "the first captured user is a disabled account");
    }

    [Fact]
    public void FromJson_DeltaPayloadAlsoParses_AndRemovedEntriesAreDetectable()
    {
        var items = Fixtures.Items("users-delta");
        var removed = items.Where(i => i.IsRemoved()).ToList();
        var live = items.Where(i => !i.IsRemoved()).Select(DirectoryUser.FromJson).ToList();

        removed.Should().NotBeEmpty("the captured delta contains tombstones");
        removed.Should().OnlyContain(r => r.GetString("id") != null);
        live.Should().NotBeEmpty();
    }

    [Fact]
    public void FromJson_ComputesPhoneDigitsLicensedAndCreated()
    {
        var u = DirectoryUser.FromJson(Fixtures.Parse("""
            {"id":"u1","userPrincipalName":"a@contoso.com","displayName":"A","accountEnabled":true,"userType":"Member",
             "businessPhones":["+1 (425) 555-0100","+44 20 7946 0958"],"mobilePhone":"+1 206-555-0200",
             "assignedLicenses":[{"skuId":"x"}],"createdDateTime":"2026-01-02T03:04:05Z","onPremisesSyncEnabled":null}
            """));

        u.PhonesDigits.Should().Be("14255550100442079460958" + "12065550200");
        u.Licensed.Should().BeTrue();
        u.IsUnlicensed.Should().BeFalse();
        u.CreatedDateTime.Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        u.OnPremisesSyncEnabled.Should().BeFalse("null means cloud-only");
        u.Badges.Should().BeEmpty();
    }

    [Fact]
    public void Badges_DisabledSyncedGuest_InOrder()
    {
        var u = DirectoryUser.FromJson(Fixtures.Parse("""
            {"id":"u2","userPrincipalName":"g_x.com#EXT#@contoso.com","accountEnabled":false,"userType":"Guest","onPremisesSyncEnabled":true,"assignedLicenses":[]}
            """));

        u.IsDisabled.Should().BeTrue();
        u.IsSynced.Should().BeTrue();
        u.IsGuest.Should().BeTrue();
        u.IsUnlicensed.Should().BeTrue();
        u.PhonesDigits.Should().BeNull();
        u.Badges.Should().Equal("Disabled", "Synced", "Guest");
    }

    [Fact]
    public void HybridRule_SyncedUser_MasteredPropertiesAreReadOnly_AlwaysWritableAreNot()
    {
        var synced = DirectoryUser.FromJson(Fixtures.Parse("""{"id":"s","userPrincipalName":"s@contoso.com","accountEnabled":true,"onPremisesSyncEnabled":true}"""));

        foreach (var p in new[] { "displayName", "givenName", "surname", "userPrincipalName", "mail", "mailNickname", "proxyAddresses", "jobTitle", "department", "companyName", "officeLocation", "employeeId", "employeeType", "businessPhones", "mobilePhone", "streetAddress", "city", "state", "postalCode", "country", "manager" })
        {
            synced.IsReadOnly(p).Should().BeTrue(p);
            synced.ReadOnlyReason(p).Should().Be("Mastered in on-prem AD (Phase 2)", p);
        }

        foreach (var p in new[] { "usageLocation", "preferredLanguage", "assignedLicenses", "passwordProfile", "accountEnabled" })
        {
            synced.IsReadOnly(p).Should().BeFalse(p);
            synced.ReadOnlyReason(p).Should().BeNull(p);
        }

        synced.IsReadOnly("JobTitle").Should().BeTrue("matching is case-insensitive");
        synced.WriteWarning("accountEnabled").Should().Be(DirectoryUser.SyncedDisableWarning);
        synced.WriteWarning("usageLocation").Should().BeNull();
    }

    [Fact]
    public void HybridRule_CloudUser_NothingIsReadOnly()
    {
        var cloud = DirectoryUser.FromJson(Fixtures.Parse("""{"id":"c","userPrincipalName":"c@contoso.com","accountEnabled":true,"onPremisesSyncEnabled":false}"""));

        DirectoryUser.OnPremMasteredProperties.Should().OnlyContain(p => !cloud.IsReadOnly(p));
        cloud.WriteWarning("accountEnabled").Should().BeNull();
    }

    [Fact]
    public void ParseRaw_RoundTripsSelectedProperties()
    {
        var u = DirectoryUser.FromJson(Fixtures.Item("users", 1));
        using var raw = u.ParseRaw();

        raw.RootElement.GetProperty("id").GetString().Should().Be(u.Id);
        raw.RootElement.TryGetProperty("proxyAddresses", out _).Should().BeTrue("detail views read fields that are not projected to columns");
    }
}
