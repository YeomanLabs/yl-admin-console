using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Devices;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Groups;
using YeomanLabs.AdminConsole.Core.Licensing;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Offboarding;
using YeomanLabs.AdminConsole.Core.Offboarding.Steps;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.Core.Tests.Offboarding;

/// <summary>
/// M6.2: the catalogued steps, with the skip rules the plan calls out — dynamic, synced and role-assignable groups,
/// group-assigned licences, and on-prem-mastered attributes.
/// </summary>
public sealed class OffboardingStepTests : IDisposable
{
    private const string UserId = "u-1";
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly GraphClient _graph;
    private readonly UserService _users;
    private readonly GroupService _groups;
    private readonly LicenseService _licenses;
    private readonly DeviceService _devices;

    public OffboardingStepTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        _graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _users = new UserService(_graph, _db, _actions, _tenant, NullLogger<UserService>.Instance);
        _groups = new GroupService(_graph, _db, _actions, _tenant, NullLogger<GroupService>.Instance);
        _licenses = new LicenseService(_graph, _db, _actions, _tenant, NullLogger<LicenseService>.Instance, (_, _) => Task.CompletedTask);
        _devices = new DeviceService(_graph, _db, _actions, _tenant, NullLogger<DeviceService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    private static DirectoryUser User(bool enabled = true, bool synced = false) => DirectoryUser.FromJson(Fixtures.Parse($$"""
        {"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":{{enabled.ToString().ToLowerInvariant()}},
         "onPremisesSyncEnabled":{{synced.ToString().ToLowerInvariant()}},"assignedLicenses":[],
         "signInActivity":{"lastSignInDateTime":"2026-09-09T09:00:00Z"} }
        """));

    private OffboardingContext Context(bool enabled = true, bool synced = false, PersonRef? delegateTo = null) => new()
    {
        User = User(enabled, synced),
        Tenant = _tenant,
        Delegate = delegateTo,
        CorrelationId = "run-1",
    };

    // ---- 1. disable-account -----------------------------------------------------------------------------------------

    [Fact]
    public async Task DisableAccount_AlreadyDisabled_IsANoOp_SoARerunSaysNothingToDo()
    {
        var step = new DisableAccountStep(_users);

        var preview = await step.PreviewAsync(Context(enabled: false), CancellationToken.None);

        preview.IsNoOp.Should().BeTrue();
        preview.CanRun.Should().BeFalse();
        preview.Summary.Should().Be("Sign-in is already disabled for ada@contoso.com");
        _handler.Requests.Should().BeEmpty("the preview reads the user it was handed, not Graph");
    }

    [Fact]
    public async Task DisableAccount_OnASyncedAccount_WarnsThatEntraConnectRevertsIt()
    {
        var step = new DisableAccountStep(_users);

        var preview = await step.PreviewAsync(Context(synced: true), CancellationToken.None);

        preview.CanRun.Should().BeTrue("Graph accepts the write; it is the next sync that undoes it");
        preview.Reason.Should().Be(DirectoryUser.SyncedDisableWarning);
    }

    [Fact]
    public async Task DisableAccount_Patches_AndHaltsTheRunIfItFails()
    {
        var step = new DisableAccountStep(_users);
        step.HaltsOnFailure.Should().BeTrue("if sign-in cannot be blocked, nothing later matters");

        _db.Write(w => w.UpsertUser(User()));
        _handler.Enqueue(HttpStatusCode.NoContent).Enqueue(HttpStatusCode.OK, """{"id":"u-1","userPrincipalName":"ada@contoso.com","accountEnabled":false,"assignedLicenses":[]}""");

        var result = await step.ExecuteAsync(Context(), CancellationToken.None);

        result.Outcome.Should().Be(StepOutcome.Success);
        result.Message.Should().Be("Sign-in disabled");
        result.CanUndo.Should().BeTrue();
        _handler.Requests[0].Body.Should().Be("""{"accountEnabled":false}""");
    }

    // ---- 3. reset-password ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResetPassword_OnASyncedTenantWithoutWriteback_SkipsWithAReason_RatherThanFailing()
    {
        var step = new ResetPasswordStep(_users);
        _handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"Request_BadRequest","message":"Password write-back is not enabled."}}""");

        var result = await step.ExecuteAsync(Context(synced: true), CancellationToken.None);

        result.Outcome.Should().Be(StepOutcome.Skipped);
        result.Message.Should().Be("Skipped — this tenant does not have password writeback enabled");
    }

    [Fact]
    public async Task ResetPassword_OnACloudAccount_ARefusalIsAGenuineFailure()
    {
        var step = new ResetPasswordStep(_users);
        _handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"Request_BadRequest","message":"The password does not comply with the tenant policy."}}""");

        var result = await step.ExecuteAsync(Context(), CancellationToken.None);

        result.Outcome.Should().Be(StepOutcome.Failed);
    }

    [Fact]
    public async Task ResetPassword_NeverLetsThePasswordReachTheActionLog()
    {
        var step = new ResetPasswordStep(_users);
        _handler.Enqueue(HttpStatusCode.NoContent);

        await step.ExecuteAsync(Context(), CancellationToken.None);

        // The step generates the password itself, so the test recovers it from the request that went out and then
        // proves that exact value appears nowhere in the row that was written.
        using var sent = System.Text.Json.JsonDocument.Parse(_handler.Requests[0].Body!);
        var password = sent.RootElement.GetProperty("passwordProfile").GetProperty("password").GetString()!;
        password.Should().HaveLength(24);

        var logged = _actions.Query(new ActionLogQuery { TargetId = UserId }).Single();
        logged.RequestSummary.Should().Be("PATCH passwordProfile forceChange=True");
        (logged.RequestSummary + logged.Message + logged.InversePayload).Should().NotContain(password);
    }

    // ---- 4. remove-auth-methods -------------------------------------------------------------------------------------

    [Fact]
    public async Task RemoveAuthMethods_LeavesThePasswordMethodAlone_AndNamesTheRest()
    {
        var step = new RemoveAuthMethodsStep(_users, _graph);
        _handler.Enqueue(HttpStatusCode.OK, """
            {"value":[
              {"@odata.type":"#microsoft.graph.passwordAuthenticationMethod","id":"pwd"},
              {"@odata.type":"#microsoft.graph.microsoftAuthenticatorAuthenticationMethod","id":"auth-1","displayName":"iPhone 15","deviceTag":"iOS"},
              {"@odata.type":"#microsoft.graph.phoneAuthenticationMethod","id":"phone-1","phoneNumber":"+1 425 555 0100","phoneType":"mobile"}]}
            """);

        var ctx = Context();
        var preview = await step.PreviewAsync(ctx, CancellationToken.None);

        preview.Summary.Should().Be("Remove 2 authentication methods");
        preview.Details.Should().BeEquivalentTo(["Microsoft Authenticator (iPhone 15 · iOS)", "Phone (+1 425 555 0100 · mobile)"]);

        _handler.Enqueue(HttpStatusCode.NoContent).Enqueue(HttpStatusCode.NoContent);
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        result.Outcome.Should().Be(StepOutcome.Success);
        _handler.Requests[1].Uri.AbsolutePath.Should().Be("/v1.0/users/u-1/authentication/microsoftAuthenticatorMethods/auth-1");
        _handler.Requests[2].Uri.AbsolutePath.Should().Be("/v1.0/users/u-1/authentication/phoneMethods/phone-1");
    }

    [Fact]
    public async Task RemoveAuthMethods_WithOnlyAPasswordRegistered_IsANoOp()
    {
        var step = new RemoveAuthMethodsStep(_users, _graph);
        _handler.Enqueue(HttpStatusCode.OK, """{"value":[{"@odata.type":"#microsoft.graph.passwordAuthenticationMethod","id":"pwd"}]}""");

        var preview = await step.PreviewAsync(Context(), CancellationToken.None);

        preview.IsNoOp.Should().BeTrue();
        preview.Summary.Should().Be("No removable authentication methods are registered");
    }

    // ---- 5. remove-manager ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RemoveManager_OnASyncedAccount_IsBlockedBecauseTheLinkIsMasteredOnPrem()
    {
        var step = new RemoveManagerStep(_graph, _actions, _tenant);

        var preview = await step.PreviewAsync(Context(synced: true), CancellationToken.None);

        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().Be(DirectoryUser.HybridReadOnlyReason);
        _handler.Requests.Should().BeEmpty("no point asking Graph for something we may not change");
    }

    [Fact]
    public void RemoveManager_IsOffByDefault_BecauseTheLinkIsOftenWhatTheNextPersonNeeds()
    {
        new RemoveManagerStep(_graph, _actions, _tenant).DefaultEnabled.Should().BeFalse();
    }

    // ---- 6. remove-group-memberships (the skip rules) ---------------------------------------------------------------

    private void EnqueueGroups(params string[] groupsJson) =>
        _handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{string.Join(",", groupsJson)}}]}""");

    private const string PlainGroup = """{"id":"g-plain","displayName":"Sales","securityEnabled":true,"groupTypes":[]}""";
    private const string DynamicGroup = """{"id":"g-dyn","displayName":"All-Staff-Dynamic","securityEnabled":true,"groupTypes":["DynamicMembership"]}""";
    private const string SyncedGroup = """{"id":"g-sync","displayName":"Finance-Users","securityEnabled":true,"groupTypes":[],"onPremisesSyncEnabled":true}""";
    private const string RoleGroup = """{"id":"g-role","displayName":"Admins","securityEnabled":true,"groupTypes":[],"isAssignableToRole":true}""";

    [Fact]
    public async Task RemoveGroups_SkipsDynamicSyncedAndRoleAssignable_AndSaysWhyForEach()
    {
        var step = new RemoveGroupMembershipsStep(_groups, _graph, _tenant);
        EnqueueGroups(PlainGroup, DynamicGroup, SyncedGroup, RoleGroup);

        var preview = await step.PreviewAsync(Context(), CancellationToken.None);

        preview.CanRun.Should().BeTrue();
        preview.Summary.Should().Be("Remove from 1 group · 3 skipped");
        preview.Details.Should().Contain(d => d.Contains("All-Staff-Dynamic") && d.Contains("Dynamic membership"));
        preview.Details.Should().Contain(d => d.Contains("Finance-Users") && d.Contains("on-prem"));
        preview.Details.Should().Contain(d => d.Contains("Admins") && d.Contains("Privileged Role Administrator"));
    }

    [Fact]
    public async Task RemoveGroups_ARoleAssignableGroupIsRemovableForAPrivilegedRoleAdministrator()
    {
        _tenant.Update(new TenantContextSnapshot
        {
            TenantId = "t-1",
            SignedInUpn = "admin@contoso.com",
            Roles = [DirectoryRoleRef.From(null, DirectoryRoleRef.PrivilegedRoleAdministrator)],
        });
        var step = new RemoveGroupMembershipsStep(_groups, _graph, _tenant);
        EnqueueGroups(RoleGroup);

        var preview = await step.PreviewAsync(Context(), CancellationToken.None);

        preview.CanRun.Should().BeTrue();
        preview.Summary.Should().Be("Remove from 1 group");
    }

    [Fact]
    public async Task RemoveGroups_EverythingSkipped_IsANoOpThatStillListsWhatWasLeftBehind()
    {
        var step = new RemoveGroupMembershipsStep(_groups, _graph, _tenant);
        EnqueueGroups(DynamicGroup, SyncedGroup);

        var preview = await step.PreviewAsync(Context(), CancellationToken.None);

        preview.IsNoOp.Should().BeTrue();
        preview.Summary.Should().Be("Nothing to remove — all 2 memberships are managed elsewhere");
        preview.Details.Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveGroups_RemovesTheCloudOnes_AndUndoPutsThemBack()
    {
        var step = new RemoveGroupMembershipsStep(_groups, _graph, _tenant);
        _db.Write(w =>
        {
            w.UpsertGroup(DirectoryGroup.FromJson(Fixtures.Parse(PlainGroup)));
            w.AddGroupMember("g-plain", UserId, "user");
        });
        EnqueueGroups(PlainGroup);

        var ctx = Context();
        await step.PreviewAsync(ctx, CancellationToken.None);

        _handler.Enqueue(HttpStatusCode.NoContent);
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        result.Outcome.Should().Be(StepOutcome.Success);
        result.Message.Should().Be("Removed from 1 group");
        _handler.Requests[1].Method.Should().Be(HttpMethod.Delete);

        _handler.Enqueue(HttpStatusCode.NoContent);
        var undo = await result.Undo!(CancellationToken.None);

        undo.Success.Should().BeTrue();
        undo.Summary.Should().Be("Re-added to 1 of 1 group");
    }

    // ---- 7. remove-licenses -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RemoveLicenses_LeavesGroupAssignedOnesAlone_AndSaysWhoWillRemoveThem()
    {
        _db.Write(w =>
        {
            w.UpsertSku(new LicenseSku { SkuId = "sku-direct", SkuPartNumber = "SPE_E3", FriendlyName = "Microsoft 365 E3", Enabled = 10, Consumed = 1, Raw = "{}" });
            w.UpsertSku(new LicenseSku { SkuId = "sku-group", SkuPartNumber = "VISIOCLIENT", FriendlyName = "Visio Plan 2", Enabled = 10, Consumed = 1, Raw = "{}" });
        });
        var step = new RemoveLicensesStep(_licenses, _graph);
        _handler.Enqueue(HttpStatusCode.OK, """
            {"licenseAssignmentStates":[
              {"skuId":"sku-direct","assignedByGroup":null,"state":"Active"},
              {"skuId":"sku-group","assignedByGroup":"grp-1","state":"Active"}]}
            """);

        var preview = await step.PreviewAsync(Context(), CancellationToken.None);

        preview.CanRun.Should().BeTrue();
        preview.Summary.Should().Be("Remove 1 licence: Microsoft 365 E3 · 1 inherited from a group (removed by the group step)");
        preview.Reason.Should().Contain("30 days", "the mailbox soft-delete window is the thing an admin needs to know before agreeing");
    }

    [Fact]
    public async Task RemoveLicenses_OnlyGroupAssigned_IsANoOp()
    {
        var step = new RemoveLicensesStep(_licenses, _graph);
        _handler.Enqueue(HttpStatusCode.OK, """{"licenseAssignmentStates":[{"skuId":"sku-group","assignedByGroup":"grp-1","state":"Active"}]}""");

        var preview = await step.PreviewAsync(Context(), CancellationToken.None);

        preview.IsNoOp.Should().BeTrue();
        preview.Summary.Should().Be("No direct licences · 1 inherited from a group, removed when the group step runs");
    }

    // ---- 8. retire-devices ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetireDevices_IsOffByDefault_AndNamesEveryDeviceItWouldRetire()
    {
        var step = new RetireDevicesStep(_devices, _graph);
        step.DefaultEnabled.Should().BeFalse("many organisations reassign the hardware instead");

        _handler.Enqueue(HttpStatusCode.OK, """
            {"value":[{"id":"d-1","deviceName":"LT-ADA-01","model":"Surface Laptop 5","operatingSystem":"Windows","complianceState":"compliant"}]}
            """);

        var preview = await step.PreviewAsync(Context(), CancellationToken.None);

        preview.Summary.Should().Be("Retire 1 device");
        preview.Details.Single().Should().StartWith("LT-ADA-01 (Surface Laptop 5, Windows");
        preview.Reason.Should().Contain("cannot be undone");
    }

    // ---- 17. update-attributes --------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAttributes_OnASyncedAccount_IsBlockedEntirely()
    {
        var step = new UpdateAttributesStep(_users);

        var preview = await step.PreviewAsync(Context(synced: true), CancellationToken.None);

        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().Be(DirectoryUser.HybridReadOnlyReason);
    }

    [Fact]
    public async Task UpdateAttributes_OnACloudAccount_StampsTheDocumentedDefaults()
    {
        var step = new UpdateAttributesStep(_users);

        var preview = await step.PreviewAsync(Context(), CancellationToken.None);

        preview.CanRun.Should().BeTrue();
        preview.Summary.Should().Be("Set 3 attributes: employee type = Offboarded, clear office, clear department");
    }

    // ---- steps that belong to a later phase -------------------------------------------------------------------------

    [Fact]
    public async Task ALaterPhaseStep_PreviewsBlockedWithItsReason_AndRefusesToRun()
    {
        var step = new NotInThisPhaseStep("hide-from-gal", "Hide from address lists", StepSurface.Exchange, OffboardingCatalog.ExchangePhase);

        var preview = await step.PreviewAsync(Context(), CancellationToken.None);

        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().Be(OffboardingCatalog.ExchangePhase);

        var act = () => step.ExecuteAsync(Context(), CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>("the runner never calls it, so a call would be a bug worth hearing about");
    }

    // ---- the catalog ------------------------------------------------------------------------------------------------

    private IReadOnlyList<IOffboardingStep> Catalog() =>
        OffboardingCatalog.Build(_users, _groups, _licenses, _devices, _graph, _actions, _tenant);

    [Fact]
    public void Catalog_HasAllNineteenStepsOfDoc06_InOrder_WithUniqueIds()
    {
        var steps = Catalog();

        steps.Should().HaveCount(19);
        steps.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        steps.Select(s => s.Id).Should().Equal([
            "disable-account", "revoke-sessions", "reset-password", "remove-auth-methods", "remove-manager",
            "remove-group-memberships", "remove-licenses", "retire-devices", "remove-registered-devices",
            "hide-from-gal", "convert-to-shared", "set-auto-reply", "forward-mail", "delegate-mailbox",
            "delegate-onedrive", "remove-teams-ownership", "update-attributes", "move-ou", "disable-ad-account",
        ]);
    }

    [Fact]
    public void Catalog_DefaultsMatchDoc06()
    {
        var on = Catalog().Where(s => s.DefaultEnabled).Select(s => s.Id);

        on.Should().Equal([
            "disable-account", "revoke-sessions", "reset-password", "remove-auth-methods",
            "remove-group-memberships", "remove-licenses", "hide-from-gal", "move-ou", "disable-ad-account",
        ]);
    }

    [Fact]
    public void Catalog_OnlyDisableAccountHaltsTheRun()
    {
        Catalog().Where(s => s.HaltsOnFailure).Select(s => s.Id).Should().Equal(["disable-account"]);
    }

    [Fact]
    public void Catalog_EveryPhase1StepIsAGraphStep_AndTheOthersAreRegisteredNotOmitted()
    {
        var catalog = Catalog();

        catalog.Where(s => s.Surface == StepSurface.Exchange).Should().HaveCount(5, "steps 10-14");
        catalog.Where(s => s.Surface == StepSurface.OnPremAd).Should().HaveCount(2, "steps 18-19");
        catalog.OfType<NotInThisPhaseStep>().Should().HaveCount(9, "5 Exchange, 2 on-prem AD, plus device deletion and OneDrive delegation");
    }
}
