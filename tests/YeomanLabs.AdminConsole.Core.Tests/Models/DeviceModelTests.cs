using FluentAssertions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Models;

public sealed class DeviceModelTests
{
    [Fact]
    public void EntraDevice_ParsesCapturedDevices_IncludingNullCompliance()
    {
        var devices = Fixtures.Items("devices").Select(EntraDevice.FromJson).ToList();

        devices.Should().HaveCountGreaterThan(1);
        devices.Should().OnlyContain(d => d.DeviceId.Length > 0);
        devices.Should().Contain(d => d.IsCompliant == true && d.IsManaged == true && d.TrustType == "AzureAd");
        devices.Should().Contain(d => d.IsCompliant == null, "a bare registered device reports null compliance");
        devices.Should().Contain(d => d.IsDisabled);
        Fixtures.Items("devices-delta").Select(EntraDevice.FromJson).Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void EntraDevice_Badges()
    {
        var d = EntraDevice.FromJson(Fixtures.Parse("""{"id":"e","deviceId":"x","accountEnabled":false,"isCompliant":false,"trustType":"ServerAd"}"""));

        d.IsHybridJoined.Should().BeTrue();
        d.Badges.Should().Equal("Disabled", "Non-compliant", "Hybrid");
        EntraDevice.FromJson(Fixtures.Parse("""{"id":"w","deviceId":"y","trustType":"Workplace"}""")).IsRegisteredOnly.Should().BeTrue();
    }

    [Fact]
    public void ManagedDevice_ParsesCapturedDevices_AndDerivesAutopilotFromProfileName()
    {
        var devices = Fixtures.Items("managed-devices").Select(ManagedDevice.FromJson).ToList();

        devices.Should().NotBeEmpty();
        var first = devices[0];
        first.AzureAdDeviceId.Should().NotBeNullOrEmpty();
        first.SerialNumber.Should().StartWith("SN-TEST-");
        first.PrimaryUserId.Should().NotBeNullOrEmpty();
        first.IsCompliant.Should().BeTrue();
        first.IsEncrypted.Should().BeTrue();
        first.OwnerType.Should().Be("company");
        first.EnrollmentType.Should().Be("windowsAzureADJoin");
        first.AutopilotEnrolled.Should().BeFalse("enrollmentProfileName is null on this device");
        first.LastSyncDateTime.Should().NotBeNull();
        first.Badges.Should().BeEmpty();
    }

    [Fact]
    public void ManagedDevice_BadgesAndStaleness()
    {
        var m = ManagedDevice.FromJson(Fixtures.Parse("""
            {"id":"m","complianceState":"noncompliant","isEncrypted":false,"enrollmentProfileName":"Standard Autopilot",
             "managedDeviceOwnerType":"personal","lastSyncDateTime":"2026-07-01T00:00:00Z","userId":""}
            """));

        m.IsNonCompliant.Should().BeTrue();
        m.AutopilotEnrolled.Should().BeTrue();
        m.IsPersonal.Should().BeTrue();
        m.PrimaryUserId.Should().BeNull("Graph sends an empty string for shared devices");
        m.Badges.Should().Equal("Non-compliant", "Not encrypted", "Autopilot", "Personal");
        m.IsStale(new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero)).Should().BeTrue();
        m.IsStale(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero)).Should().BeFalse();

        var grace = ManagedDevice.FromJson(Fixtures.Parse("""{"id":"g","complianceState":"inGracePeriod"}"""));
        grace.Badges.Should().Equal("Grace period");
        grace.AutopilotEnrolled.Should().BeNull("property absent from payload");
    }
}
