using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Devices;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Devices;

/// <summary>
/// M5.6: Sync, Restart, Retire, Wipe, and the two secret reveals. These are exercised only here — per the owner's
/// instruction, Retire and Wipe are built and tested but never fired at a real device.
/// </summary>
public sealed class DeviceServiceActionTests : IDisposable
{
    private const string Id = "d-1";
    private const string AadDeviceId = "aad-dev-1";
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly DeviceService _sut;

    public DeviceServiceActionTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new DeviceService(graph, _db, _actions, _tenant, NullLogger<DeviceService>.Instance);
        _db.Write(w => w.UpsertManagedDevice(ManagedDevice.FromJson(Fixtures.Parse(
            $$"""{"id":"{{Id}}","deviceName":"LAPTOP-42","azureADDeviceId":"{{AadDeviceId}}","operatingSystem":"Windows","complianceState":"compliant"}"""))));
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    private ActionRecord Logged() => _actions.Query(new ActionLogQuery { TargetId = Id }).Single();

    [Theory]
    [InlineData("sync", "syncDevice", "device.sync", "Sync requested for LAPTOP-42")]
    [InlineData("restart", "rebootNow", "device.restart", "Restart sent to LAPTOP-42")]
    [InlineData("retire", "retire", "device.retire", "Retired LAPTOP-42")]
    public async Task Action_PostsToTheRightEndpoint_AndLogsAnIrreversibleAction(string which, string endpoint, string logAction, string summary)
    {
        _handler.Enqueue(HttpStatusCode.NoContent);

        var r = which switch
        {
            "sync" => await _sut.SyncAsync(Id, CancellationToken.None),
            "restart" => await _sut.RestartAsync(Id, CancellationToken.None),
            _ => await _sut.RetireAsync(Id, CancellationToken.None),
        };

        r.Success.Should().BeTrue();
        r.Summary.Should().Be(summary);
        r.CanUndo.Should().BeFalse("Intune has no inverse for any of these");

        _handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _handler.Requests[0].Uri.AbsolutePath.Should().Be($"/v1.0/deviceManagement/managedDevices/d-1/{endpoint}");

        var logged = Logged();
        logged.Action.Should().Be(logAction);
        logged.TargetType.Should().Be(ObjectType.Device);
        logged.TargetName.Should().Be("LAPTOP-42");
        logged.IsReversible.Should().BeFalse();
    }

    [Fact]
    public async Task Wipe_SendsBothFlags_AndRecordsThemInTheActionLog()
    {
        _handler.Enqueue(HttpStatusCode.NoContent);

        var r = await _sut.WipeAsync(Id, keepEnrollmentData: false, keepUserData: false, CancellationToken.None);

        r.Success.Should().BeTrue();
        r.Summary.Should().Be("Wipe sent to LAPTOP-42");
        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/deviceManagement/managedDevices/d-1/wipe");
        _handler.Requests[0].Body.Should().Be("""{"keepEnrollmentData":false,"keepUserData":false}""");
        Logged().RequestSummary.Should().Be("POST wipe keepEnrollmentData=False keepUserData=False");
    }

    [Fact]
    public async Task Wipe_KeepingUserData_SendsTheFlagThrough()
    {
        _handler.Enqueue(HttpStatusCode.NoContent);

        await _sut.WipeAsync(Id, keepEnrollmentData: true, keepUserData: true, CancellationToken.None);

        _handler.Requests[0].Body.Should().Be("""{"keepEnrollmentData":true,"keepUserData":true}""");
    }

    [Fact]
    public async Task Action_GraphRejects_MapsTheErrorWithTheIntuneRoleHint()
    {
        _handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""");

        var r = await _sut.SyncAsync(Id, CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("You don't have permission for this (Intune Administrator).");
        Logged().Outcome.Should().Be(ActionOutcome.Failed);
    }

    // ---- BitLocker ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetBitLockerKeys_FiltersOnTheEntraDeviceId_AndAsksForNoKeyMaterial()
    {
        _handler.Enqueue(HttpStatusCode.OK, """
            {"value":[{"id":"k-2","volumeType":"fixedDataVolume","createdDateTime":"2026-02-01T10:00:00Z"},
                      {"id":"k-1","volumeType":"operatingSystemVolume","createdDateTime":"2026-01-01T10:00:00Z"}]}
            """);

        var keys = await _sut.GetBitLockerKeysAsync(AadDeviceId, CancellationToken.None);

        var query = Uri.UnescapeDataString(_handler.Requests[0].Uri.Query);
        query.Should().Contain("deviceId eq 'aad-dev-1'");
        query.Should().NotContain(",key", "listing must never pull key material");
        keys.Should().HaveCount(2);
        keys[0].VolumeLabel.Should().Be("Fixed data drive");
        keys.Should().OnlyContain(k => k.Key == null);
    }

    [Fact]
    public async Task RevealBitLockerKey_ReturnsTheKey_AndLogsTheRevealWithoutIt()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"id":"k-1","volumeType":"operatingSystemVolume","key":"123456-654321-111111"}""");

        var r = await _sut.RevealBitLockerKeyAsync(Id, "k-1", CancellationToken.None);

        r.Success.Should().BeTrue();
        r.Value!.Key.Should().Be("123456-654321-111111");

        var logged = Logged();
        logged.Action.Should().Be("device.bitlocker.reveal");
        logged.Outcome.Should().Be(ActionOutcome.Success);
        logged.Message.Should().Be("Revealed the Operating system drive recovery key");
        (logged.RequestSummary + logged.Message).Should().NotContain("123456", "the key must never reach the action log");
    }

    [Fact]
    public async Task RevealBitLockerKey_AsksForKeyAlone_BecauseAnyOtherSelectDropsIt()
    {
        // Verified on the dev tenant: "$select=id,volumeType,key" comes back without the key. Only "$select=key" works.
        _handler.Enqueue(HttpStatusCode.OK, """{"id":"k-1","volumeType":"1","key":"123456-654321-111111"}""");

        await _sut.RevealBitLockerKeyAsync(Id, "k-1", CancellationToken.None);

        Uri.UnescapeDataString(_handler.Requests[0].Uri.Query).Should().Be("?$select=key");
    }

    [Fact]
    public async Task RevealBitLockerKey_Forbidden_ExplainsTheRoleAndLogsTheAttempt()
    {
        _handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""");

        var r = await _sut.RevealBitLockerKeyAsync(Id, "k-1", CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("You don't have permission for this (Cloud Device Administrator).");
        Logged().Outcome.Should().Be(ActionOutcome.Failed);
    }

    // ---- LAPS --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RevealLaps_DecodesTheBase64Password_AndLogsOnlyTheAccountName()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Tr0ub4dor&3"));
        _handler.Enqueue(HttpStatusCode.OK, $$"""
            {"credentials":[{"accountName":"LapsAdmin","passwordBase64":"{{encoded}}","backupDateTime":"2026-09-01T08:00:00Z"}]}
            """);

        var r = await _sut.RevealLapsAsync(Id, "entra-obj-1", CancellationToken.None);

        r.Success.Should().BeTrue();
        r.Value!.Single().AccountName.Should().Be("LapsAdmin");
        r.Value!.Single().Password.Should().Be("Tr0ub4dor&3");

        var logged = Logged();
        logged.Action.Should().Be("device.laps.reveal");
        logged.Message.Should().Be("Revealed the LAPS password for LapsAdmin");
        (logged.RequestSummary + logged.Message).Should().NotContain("Tr0ub4dor", "the password must never reach the action log");
    }

    [Fact]
    public async Task RevealLaps_NoBackupOnThisDevice_SaysSoInsteadOfFailing()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"credentials":[]}""");

        var r = await _sut.RevealLapsAsync(Id, "entra-obj-1", CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("No LAPS password is backed up for this device.");
        Logged().Outcome.Should().Be(ActionOutcome.Skipped);
    }

    [Fact]
    public async Task RevealLaps_EmptyBody_IsTreatedAsNoBackupRatherThanAnError()
    {
        // Verified against the dev tenant: a device with no LAPS backup answers 200 with no body at all.
        _handler.Enqueue(HttpStatusCode.OK, string.Empty);

        var r = await _sut.RevealLapsAsync(Id, AadDeviceId, CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("No LAPS password is backed up for this device.");
        r.Error.Should().BeNull("an absent backup is not a Graph failure");
    }

    [Fact]
    public async Task RevealLaps_UsesTheEntraDeviceId_NotTheDirectoryObjectId()
    {
        // The directory object id answers 400 "could not be found" on the live tenant; doc 04 was corrected.
        _handler.Enqueue(HttpStatusCode.OK, """{"credentials":[]}""");

        await _sut.RevealLapsAsync(Id, AadDeviceId, CancellationToken.None);

        _handler.Requests[0].Uri.AbsolutePath.Should().Be($"/v1.0/directory/deviceLocalCredentials/{AadDeviceId}");
    }

    [Theory]
    [InlineData("1", "Operating system drive")]
    [InlineData("2", "Fixed data drive")]
    [InlineData("operatingSystemVolume", "Operating system drive")]
    [InlineData(null, "Drive")]
    public void BitLockerVolumeLabel_HandlesGraphsNumericEnumStrings(string? volumeType, string expected)
    {
        // Graph returns volumeType as "1"/"2" on the live tenant, not the member name.
        new BitLockerKeyRef { Id = "k", VolumeType = volumeType }.VolumeLabel.Should().Be(expected);
    }

    [Fact]
    public void LapsCredential_KeepsThePasswordOutOfItsOwnToString()
    {
        var credential = new LapsCredential { AccountName = "LapsAdmin", Password = "Tr0ub4dor&3" };

        credential.ToString().Should().NotContain("Tr0ub4dor").And.Contain("***");
    }

    [Fact]
    public void LapsCredential_PassesThroughAValueThatIsNotBase64()
    {
        LapsCredential.Decode("not base64!").Should().Be("not base64!", "a tenant quirk must not break the reveal");
        LapsCredential.Decode(null).Should().BeEmpty();
    }
}
