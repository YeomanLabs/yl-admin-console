using FluentAssertions;
using Xunit;
using AdminConsole.Core.Licensing;
using AdminConsole.Core.Models;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Models;

public sealed class LicenseAndOrganizationTests
{
    [Fact]
    public void LicenseSku_ParsesCapturedSkus_WithFriendlyNamesWhereKnown()
    {
        var skus = Fixtures.Items("skus").Select(LicenseSku.FromJson).ToList();

        skus.Should().HaveCountGreaterThan(1);
        skus.Should().OnlyContain(s => s.SkuPartNumber.Length > 0 && s.Enabled >= 0);
        var flow = skus.Single(s => s.SkuPartNumber == "FLOW_FREE");
        flow.FriendlyName.Should().Be("Power Automate Free");
        flow.DisplayName.Should().Be("Power Automate Free");
        flow.Enabled.Should().Be(10000);
        flow.Consumed.Should().Be(1);
        flow.Available.Should().Be(9999);
        flow.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void LicenseSku_UnknownPartNumber_FallsBackToPartNumber()
    {
        var s = LicenseSku.FromJson(Fixtures.Parse("""{"skuId":"1","skuPartNumber":"MYSTERY_SKU","consumedUnits":7,"prepaidUnits":{"enabled":5},"capabilityStatus":"Suspended"}"""));

        s.FriendlyName.Should().BeNull();
        s.DisplayName.Should().Be("MYSTERY_SKU");
        s.Available.Should().Be(0, "never negative");
        s.IsEnabled.Should().BeFalse();
        SkuNames.For("SPE_E3").Should().Be("Microsoft 365 E3");
        SkuNames.For("spe_e3").Should().Be("Microsoft 365 E3", "case-insensitive");
        SkuNames.For(null).Should().BeNull();
    }

    [Fact]
    public void Organization_ParsesCapturedTenant_DetectsP1AndP2()
    {
        var org = Organization.FromJson(Fixtures.Item("organization", 0));

        org.Id.Should().Be("c570b99a-c52d-47f4-997e-59c3b3205cf6");
        org.DisplayName.Should().Be("Contoso Ltd");
        org.HasP1.Should().BeTrue();
        org.HasP2.Should().BeTrue();
        org.OnPremisesSyncEnabled.Should().BeFalse("null → cloud-only");
        org.VerifiedDomains.Should().Contain("yeomanlabs.co");
    }

    [Fact]
    public void Organization_DeletedPlanDoesNotCount()
    {
        var org = Organization.FromJson(Fixtures.Parse("""
            {"id":"t","assignedPlans":[{"servicePlanId":"41781fb2-bc02-4b7c-bd55-b576c07bb09d","capabilityStatus":"Deleted"}],"onPremisesSyncEnabled":true,"onPremisesLastSyncDateTime":"2026-09-09T01:00:00Z"}
            """));

        org.HasP1.Should().BeFalse();
        org.OnPremisesSyncEnabled.Should().BeTrue();
        org.OnPremisesLastSyncDateTime.Should().Be(new DateTimeOffset(2026, 9, 9, 1, 0, 0, TimeSpan.Zero));
    }
}
