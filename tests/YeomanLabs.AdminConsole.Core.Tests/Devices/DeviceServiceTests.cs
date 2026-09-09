using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Devices;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Devices;

public sealed class DeviceServiceTests : IDisposable
{
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly DeviceService _sut;

    public DeviceServiceTests()
    {
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new DeviceService(graph, _db, _actions, _tenant, NullLogger<DeviceService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    [Fact]
    public async Task Hydrate_OneBatch_ThenSettingStatesOnlyForFailingPolicies()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, Batch(
                ("device", 200, """{"id":"m1","deviceName":"PC-1","complianceState":"noncompliant","userId":"u1","managedDeviceOwnerType":"company"}"""),
                ("users", 200, """{"value":[{"id":"u1","displayName":"Ada","userPrincipalName":"ada@contoso.com"},{"id":"u2","displayName":"Grace","userPrincipalName":"grace@contoso.com"}]}"""),
                ("policies", 200, """{"value":[{"id":"p-ok","displayName":"Baseline","state":"compliant","version":3,"platformType":"windows10AndLater","settingCount":4},{"id":"p-bad","displayName":"Encryption","state":"noncompliant","version":1,"platformType":"windows10AndLater","settingCount":1},{"id":"p-na","displayName":"iOS policy","state":"notApplicable"}]}""")))
            .Enqueue(HttpStatusCode.OK, Batch(
                ("p-bad", 200, """{"value":[{"id":"s1","setting":"Windows10CompliancePolicy.BitLockerEnabled","settingName":"BitLocker","state":"noncompliant","errorDescription":"BitLocker is not enabled"},{"id":"s2","setting":"X","state":"compliant"}]}""")));

        var d = await _sut.HydrateAsync("m1", CancellationToken.None);

        _handler.Requests.Should().HaveCount(2);
        using var first = JsonDocument.Parse(_handler.Requests[0].Body!);
        first.RootElement.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("url").GetString()!).Should().Equal(
            "/deviceManagement/managedDevices/m1?$select=" + GraphSelects.ManagedDevice,
            "/deviceManagement/managedDevices/m1/users?$select=id,displayName,userPrincipalName",
            "/deviceManagement/managedDevices/m1/deviceCompliancePolicyStates");
        using var second = JsonDocument.Parse(_handler.Requests[1].Body!);
        second.RootElement.GetProperty("requests").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("url").GetString().Should().Be("/deviceManagement/managedDevices/m1/deviceCompliancePolicyStates/p-bad/settingStates");

        d.Device.Value!.DeviceName.Should().Be("PC-1");
        d.Users.Value.Should().HaveCount(2);
        var policies = d.CompliancePolicies.Value!;
        policies.Should().HaveCount(3);
        policies[0].IsCompliant.Should().BeTrue();
        policies[0].SettingStates.Should().BeEmpty();
        policies[1].SettingStates.Should().ContainSingle("only the failing setting is kept").Which.ErrorDescription.Should().Be("BitLocker is not enabled");
        policies[2].IsNotApplicable.Should().BeTrue();
        policies[2].NeedsReason.Should().BeFalse();
        _db.GetManagedDevice("m1")!.DeviceName.Should().Be("PC-1", "write-through");
    }

    [Fact]
    public async Task Hydrate_PolicyRead403_FailsThatSectionOnly_NoSecondBatch()
    {
        _handler.Enqueue(HttpStatusCode.OK, Batch(
            ("device", 200, """{"id":"m1","deviceName":"PC-1","complianceState":"compliant"}"""),
            ("users", 200, """{"value":[]}"""),
            ("policies", 403, """{"error":{"code":"Authorization_RequestDenied","message":"needs DeviceManagementConfiguration.Read.All"}}""")));

        var d = await _sut.HydrateAsync("m1", CancellationToken.None);

        d.Device.IsSuccess.Should().BeTrue();
        d.CompliancePolicies.IsForbidden.Should().BeTrue();
        _handler.Requests.Should().HaveCount(1);
    }

    private static string Batch(params (string Id, int Status, string Body)[] inner) =>
        "{\"responses\":[" + string.Join(",", inner.Select(i => $"{{\"id\":\"{i.Id}\",\"status\":{i.Status},\"body\":{i.Body}}}")) + "]}";
}
