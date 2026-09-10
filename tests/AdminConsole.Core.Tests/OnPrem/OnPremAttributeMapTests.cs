using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;
using FluentAssertions;
using Xunit;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>
/// The Graph-to-LDAP map behind the hybrid unlock (M12.4).
/// </summary>
/// <remarks>
/// The failure this guards against is silent: writing a plausible-looking attribute name that the directory does not
/// have, or one it does have that Entra Connect does not read, so the change appears to work and never reaches the
/// cloud. Every case below is a name Graph renamed.
/// </remarks>
public sealed class OnPremAttributeMapTests
{
    [Fact]
    public void Covers_every_property_the_ui_greys()
    {
        // If a property is added to the mastered list and not to the map, the field would unlock into a write that
        // has nowhere to go. This is the check that keeps the two in step.
        DirectoryUser.OnPremMasteredProperties.Should().BeSubsetOf(OnPremAttributeMap.GraphToLdap.Keys);
    }

    [Theory]
    [InlineData("surname", "sn")]
    [InlineData("jobTitle", "title")]
    [InlineData("companyName", "company")]
    [InlineData("officeLocation", "physicalDeliveryOfficeName")]
    [InlineData("employeeId", "employeeID")]
    [InlineData("city", "l")]
    [InlineData("state", "st")]
    [InlineData("mobilePhone", "mobile")]
    [InlineData("country", "co")]
    [InlineData("displayName", "displayName")]
    public void Resolves_the_ldap_name(string graphProperty, string expected)
    {
        OnPremAttributeMap.Translate(graphProperty, "x").LdapName.Should().Be(expected);
    }

    [Fact]
    public void Country_is_the_name_attribute_not_the_code()
    {
        // c holds "GB" and countryCode holds 826; Entra Connect syncs co. Writing either of the others would leave
        // the cloud showing the old value for ever.
        OnPremAttributeMap.GraphToLdap["country"].Should().Be("co");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_edit_clears_rather_than_writing_blank(string? typed)
    {
        // The same trap Graph sprang with department: "" is a value, not the absence of one.
        var result = OnPremAttributeMap.Translate("department", typed);
        result.CanWrite.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Trims_what_was_typed()
    {
        OnPremAttributeMap.Translate("department", "  Sales  ").Value.Should().Be("Sales");
    }

    [Fact]
    public void One_business_phone_writes_to_telephoneNumber()
    {
        var result = OnPremAttributeMap.Translate("businessPhones", new[] { "+44 20 7946 0000" });
        result.LdapName.Should().Be("telephoneNumber");
        result.Value.Should().Be("+44 20 7946 0000");
    }

    [Fact]
    public void Several_business_phones_are_refused_rather_than_truncated()
    {
        // Graph holds a list; AD's telephoneNumber holds one. Writing the first and dropping the rest would look
        // exactly like a success.
        var result = OnPremAttributeMap.Translate("businessPhones", new[] { "+44 1", "+44 2" });
        result.CanWrite.Should().BeFalse();
        result.Refusal.Should().Contain("one business phone");
    }

    [Fact]
    public void An_empty_phone_list_clears_the_attribute()
    {
        var result = OnPremAttributeMap.Translate("businessPhones", Array.Empty<string>());
        result.CanWrite.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Manager_is_refused_because_ad_stores_a_distinguished_name()
    {
        var result = OnPremAttributeMap.Translate("manager", "Ada Lovelace");
        result.CanWrite.Should().BeFalse();
        result.Refusal.Should().Contain("distinguished name");
        OnPremAttributeMap.EditableLdapName("manager").Should().BeNull();
    }

    [Fact]
    public void A_cloud_only_property_is_refused()
    {
        // usageLocation and preferredLanguage are mastered in Entra, so they must keep going to Graph.
        OnPremAttributeMap.Translate("usageLocation", "GB").CanWrite.Should().BeFalse();
        OnPremAttributeMap.EditableLdapName("preferredLanguage").Should().BeNull();
    }

    [Fact]
    public void Property_names_are_matched_regardless_of_case()
    {
        OnPremAttributeMap.EditableLdapName("JOBTITLE").Should().Be("title");
    }
}
