using FluentAssertions;
using Xunit;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Cache.Browse;
using AdminConsole.Core.Licensing;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Tests.Cache;

/// <summary>M18.4: <c>key=value</c> in a list's filter box, each key a fixed SQL fragment, unknown keys said out loud.</summary>
public sealed class FieldQueryTests : IDisposable
{
    private readonly CacheDb _db = CacheDb.OpenInMemory();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Parse_SplitsFieldsFromFreeText_AndKeepsQuotedSpaces()
    {
        var q = FieldQuery.Parse("ada dep=\"field services\" disabled=true", UserBrowse.Fields);

        q.FreeText.Should().Be("ada");
        q.Conditions.Should().HaveCount(2);
        q.Conditions[0].Value.Should().Be("field services");
        q.Conditions[1].Value.Should().BeNull("a yes/no field picks a constant fragment and binds nothing");
        q.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnknownKey_IsReportedWithTheKeysThatDoExist_NotIgnored()
    {
        var q = FieldQuery.Parse("colour=blue", UserBrowse.Fields);

        q.Conditions.Should().BeEmpty();
        q.Problems.Should().ContainSingle().Which.Should().Contain("\"colour\" isn't a field here").And.Contain("dep");
    }

    [Fact]
    public void Parse_AFlagWithAValueItCannotRead_SaysWhatItTakes()
    {
        FieldQuery.Parse("disabled=maybe", UserBrowse.Fields).Problems.Single().Should().Contain("true or false");
        FieldQuery.Parse("dep=", UserBrowse.Fields).Problems.Single().Should().Contain("needs a value");
    }

    [Fact]
    public void Parse_AnAddressWithAnEqualsSign_IsFreeText()
    {
        // Only a bare word before = is a key; "a+b=c@x" is something an admin pasted.
        FieldQuery.Parse("a+b=c@contoso.com", UserBrowse.Fields).FreeText.Should().Be("a+b=c@contoso.com");
    }

    [Fact]
    public void Parse_Aliases_MeanTheSameField()
    {
        FieldQuery.Parse("department=sales", UserBrowse.Fields).Conditions.Should().ContainSingle();
    }

    [Fact]
    public void BrowseUsers_DepAndFlag_NarrowTheSameAsTheChipsWould()
    {
        Seed();

        var byField = _db.BrowseUsers(new BrowseQuery { Text = "dep=eng disabled=false" });
        byField.Rows.Select(r => r.DisplayName).Should().Equal(["Ada Lovelace"]);

        var byChip = _db.BrowseUsers(new BrowseQuery { Text = "dep=eng", FilterKeys = ["enabled"] });
        byChip.Rows.Select(r => r.Id).Should().Equal(byField.Rows.Select(r => r.Id), "a field query and the same filter as a chip agree");
    }

    [Fact]
    public void BrowseUsers_Lic_MatchesThePartNumberOrTheFriendlyName()
    {
        Seed();

        _db.BrowseUsers(new BrowseQuery { Text = "lic=e3" }).Rows.Select(r => r.DisplayName).Should().Equal(["Ada Lovelace"]);
        _db.BrowseUsers(new BrowseQuery { Text = "lic=\"Microsoft 365 E3\"" }).Rows.Should().ContainSingle();
        _db.BrowseUsers(new BrowseQuery { Text = "lic=visio" }).Rows.Should().BeEmpty();
    }

    [Fact]
    public void BrowseUsers_AValueIsBound_SoSqlInItIsJustText()
    {
        Seed();

        var page = _db.BrowseUsers(new BrowseQuery { Text = "dep=\"x' OR 1=1 --\"" });

        page.Rows.Should().BeEmpty("the value is a parameter, never part of the statement");
        _db.BrowseUsers(new BrowseQuery()).Total.Should().Be(3, "and the table is untouched");
    }

    [Fact]
    public void BrowseUsers_ProblemsComeBackWithThePage_AndTheRestOfTheQueryStillApplies()
    {
        Seed();

        var page = _db.BrowseUsers(new BrowseQuery { Text = "colour=blue dep=sales" });

        page.Problems.Should().ContainSingle();
        page.Rows.Select(r => r.DisplayName).Should().Equal(["Dee Sales"]);
    }

    [Fact]
    public void BrowseDevices_Compliance_MatchesWholeValues_SoCompliantIsNotNoncompliant()
    {
        _db.Write(w =>
        {
            w.UpsertManagedDevice(new ManagedDevice { Id = "m1", AzureAdDeviceId = "a1", DeviceName = "LT-01", OperatingSystem = "Windows", ComplianceState = "compliant", Raw = "{}" });
            w.UpsertManagedDevice(new ManagedDevice { Id = "m2", AzureAdDeviceId = "a2", DeviceName = "LT-02", OperatingSystem = "Windows", ComplianceState = "noncompliant", Raw = "{}" });
        });

        _db.BrowseDevices(new BrowseQuery { Text = "compliance=compliant" }).Rows.Select(r => r.Primary).Should().Equal(["LT-01"]);
        _db.BrowseDevices(new BrowseQuery { Text = "compliance=NonCompliant" }).Rows.Select(r => r.Primary).Should().Equal(["LT-02"], "still case-insensitive");
        _db.BrowseDevices(new BrowseQuery { Text = "os=win" }).Rows.Should().HaveCount(2, "a text field still matches part of a value");
    }

    private void Seed() => _db.Write(w =>
    {
        w.UpsertSku(new LicenseSku { SkuId = "sku-e3", SkuPartNumber = "SPE_E3", FriendlyName = "Microsoft 365 E3", Enabled = 10, Consumed = 1, Raw = "{}" });
        w.UpsertUser(new DirectoryUser
        {
            Id = "u1", UserPrincipalName = "ada@contoso.com", DisplayName = "Ada Lovelace", Department = "Engineering",
            AccountEnabled = true, UserType = "Member", OnPremisesSyncEnabled = false, Licensed = true,
            Raw = """{"assignedLicenses":[{"skuId":"sku-e3","disabledPlans":[]}]}""",
        });
        w.UpsertUser(new DirectoryUser
        {
            Id = "u2", UserPrincipalName = "cy@contoso.com", DisplayName = "Cy Disabled", Department = "Engineering",
            AccountEnabled = false, UserType = "Member", OnPremisesSyncEnabled = false, Licensed = false, Raw = """{"assignedLicenses":[]}""",
        });
        w.UpsertUser(new DirectoryUser
        {
            Id = "u3", UserPrincipalName = "dee@contoso.com", DisplayName = "Dee Sales", Department = "Sales",
            AccountEnabled = true, UserType = "Member", OnPremisesSyncEnabled = false, Licensed = false, Raw = "{}",
        });
    });
}
