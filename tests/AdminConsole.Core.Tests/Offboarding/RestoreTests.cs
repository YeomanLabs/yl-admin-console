using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Groups;
using AdminConsole.Core.Licensing;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Restore;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Tests.Exchange;
using AdminConsole.Core.Tests.Fakes;
using AdminConsole.Core.Users;

namespace AdminConsole.Core.Tests.Offboarding;

/// <summary>
/// M15.4 — restore from a snapshot: an offboarding in reverse. Each step puts back only what differs from the
/// snapshot, so the tests are mostly about the difference being worked out right.
/// </summary>
public sealed class RestoreTests : IDisposable
{
    private const string UserId = "00000000-0000-0000-0000-000000000104";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeHttpHandler _graphHandler = new();
    private readonly FakeHttpHandler _exo = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly GraphClient _graph;
    private readonly UserService _users;

    public RestoreTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        _graph = new GraphClient(new HttpClient(_graphHandler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _users = new UserService(_graph, _db, _actions, _tenant, NullLogger<UserService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    private static OffboardingSnapshot Snapshot(SnapshotOnPrem? onPrem = null, SnapshotMailbox? mailbox = null, string? correlation = "run-1") => new()
    {
        TenantId = "t-1",
        UserId = UserId,
        UserPrincipalName = "Adele@contoso.com",
        DisplayName = "Adele Vance",
        TakenAt = DateTimeOffset.UtcNow,
        CorrelationId = correlation,
        AccountEnabled = true,
        Synced = onPrem is not null,
        Groups =
        [
            new SnapshotGroup("g-cloud", "Sales", "Security", Synced: false, Dynamic: false, MailEnabledList: false),
            new SnapshotGroup("g-synced", "Finance (AD)", "Security", Synced: true, Dynamic: false, MailEnabledList: false),
            new SnapshotGroup("g-dynamic", "All Staff", "Security", Synced: false, Dynamic: true, MailEnabledList: false),
            new SnapshotGroup("dl-1", "Sales List", "DistributionOrMailSecurity", Synced: false, Dynamic: false, MailEnabledList: true),
        ],
        Licences = [new SnapshotRef("sku-e3", "Microsoft 365 E3")],
        Attributes = new Dictionary<string, string?> { ["department"] = "Sales", ["employeeType"] = null },
        OnPrem = onPrem,
        Mailbox = mailbox,
    };

    private OffboardingContext Context(OffboardingSnapshot snapshot, bool enabled = false, bool synced = false, string? ou = null) => new()
    {
        User = new DirectoryUser
        {
            Id = UserId, UserPrincipalName = "Adele@contoso.com", DisplayName = "Adele Vance", AccountEnabled = enabled,
            OnPremisesSyncEnabled = synced, Licensed = false, Raw = "{}",
        },
        Tenant = _tenant,
        CorrelationId = "restore-1",
        RestoreFrom = snapshot,
        RestoreOuDn = ou,
    };

    private static string Batch(params (string Id, int Status, string Body)[] inner) =>
        "{\"responses\":[" + string.Join(",", inner.Select(i => $"{{\"id\":\"{i.Id}\",\"status\":{i.Status},\"body\":{i.Body}}}")) + "]}";

    /// <summary>The account after an offboarding: no groups but a dynamic one, no licences, department stamped empty.</summary>
    private static string Offboarded() => Batch(
        ("user", 200, """{"id":"00000000-0000-0000-0000-000000000104","displayName":"Adele Vance","userPrincipalName":"Adele@contoso.com","accountEnabled":false,"employeeType":"Offboarded"}"""),
        ("manager", 404, """{"error":{"code":"Request_ResourceNotFound","message":"no manager"}}"""),
        ("memberOf", 200, """{"value":[{"@odata.type":"#microsoft.graph.group","id":"g-dynamic","displayName":"All Staff","groupTypes":["DynamicMembership"],"securityEnabled":true,"mailEnabled":false}]}"""),
        ("auth", 200, """{"value":[]}"""),
        ("devices", 200, """{"value":[]}"""),
        ("licenses", 200, """{"id":"x","licenseAssignmentStates":[]}"""),
        ("reports", 200, """{"value":[]}"""));

    private IReadOnlyList<IOffboardingStep> Catalog(IDirectoryService? directory = null) => RestoreCatalog.Build(
        _users,
        new GroupService(_graph, _db, _actions, _tenant, NullLogger<GroupService>.Instance),
        new LicenseService(_graph, _db, _actions, _tenant, NullLogger<LicenseService>.Instance, (_, _) => Task.CompletedTask),
        _graph, _actions, _tenant, directory ?? new FakeDirectoryService(), MailboxSummaryTests.Service(_exo, actions: _actions),
        new AdminConsole.Core.OneDrive.OneDriveService(_graph, new AdminConsole.Core.SharePoint.SharePointClient(new HttpClient(_graphHandler), new FakeAuthService(),
            NullLogger<AdminConsole.Core.SharePoint.SharePointClient>.Instance), _tenant, _actions, NullLogger<AdminConsole.Core.OneDrive.OneDriveService>.Instance));

    private IOffboardingStep Step(string id, IDirectoryService? directory = null) => Catalog(directory).Single(s => s.Id == id);

    // ---- the catalog ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_restore_moves_the_ad_account_before_enabling_it_and_licences_the_mailbox_before_converting_it()
    {
        var ids = Catalog().Select(s => s.Id).ToList();

        ids.Should().Equal([
            "restore-ad-ou", "restore-ad-account", "restore-ad-groups", "restore-sign-in", "restore-licences", "restore-mailbox-type",
            "restore-groups", "restore-distribution-lists", "restore-manager", "restore-attributes", "restore-mailbox-settings",
            "restore-other-mailbox-access", "restore-onedrive", "restore-not-restorable",
        ]);
        Catalog().Should().NotContain(s => s.HaltsOnFailure || s.IsRequired);
    }

    [Fact]
    public async Task Without_a_snapshot_every_step_says_so()
    {
        var ctx = Context(Snapshot()) is var c ? new OffboardingContext { User = c.User, Tenant = c.Tenant, CorrelationId = c.CorrelationId } : null!;

        var preview = await Step("restore-sign-in").PreviewAsync(ctx, Ct);

        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().Be("No snapshot was chosen.");
    }

    // ---- AD ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_ad_account_goes_back_to_its_ou_enabled_and_in_its_groups()
    {
        var ad = new FakeDirectoryService();
        var lab = ad.AddOu("Lab");
        var users = ad.AddOu("Users", lab.DistinguishedName);
        var disabled = ad.AddOu("Disabled", lab.DistinguishedName);
        var leaver = ad.AddUser("adele.vance", users.DistinguishedName);
        var finance = ad.AddGroup("Finance");
        (await ad.AddToGroupAsync(finance.ObjectGuid, leaver.ObjectGuid, Ct)).Success.Should().BeTrue();
        var before = (await ad.GetUserAsync(leaver.ObjectGuid, Ct))!;
        var snapshot = Snapshot(onPrem: new SnapshotOnPrem
        {
            ObjectGuid = leaver.ObjectGuid, SamAccountName = "adele.vance", DistinguishedName = before.DistinguishedName,
            ParentDn = users.DistinguishedName, Enabled = true, GroupDns = before.MemberOfDns,
        });

        // What an offboarding did to it.
        await ad.RemoveFromGroupAsync(finance.ObjectGuid, leaver.ObjectGuid, Ct);
        await ad.MoveAsync(leaver.ObjectGuid, disabled.DistinguishedName, Ct);
        await ad.SetEnabledAsync(leaver.ObjectGuid, false, Ct);

        var runner = new OffboardingRunner(Catalog(ad).Where(s => s.Surface == StepSurface.OnPremAd), NullLogger<OffboardingRunner>.Instance);
        var ctx = Context(snapshot, synced: true);
        var previews = await runner.PreviewAsync(ctx, runner.Steps.Select(s => s.Id).ToList(), Ct);

        previews.Select(p => p.Summary).Should().Equal(
            "Move adele.vance from Lab / Disabled to Lab / Users",
            "Enable adele.vance in Active Directory",
            "Add adele.vance back to 1 AD group");

        var run = await runner.ExecuteAsync(ctx, previews, null, Ct);

        run.Results.Should().OnlyContain(r => r.Outcome == StepOutcome.Success);
        var after = (await ad.GetUserAsync(leaver.ObjectGuid, Ct))!;
        DistinguishedNames.Parent(after.DistinguishedName).Should().Be(users.DistinguishedName);
        after.Enabled.Should().BeTrue();
        after.MemberOfDns.Should().Equal(finance.DistinguishedName);
    }

    [Fact]
    public async Task A_chosen_ou_overrides_the_recorded_one_and_the_preview_says_so()
    {
        var ad = new FakeDirectoryService();
        var recorded = ad.AddOu("Users");
        var elsewhere = ad.AddOu("Rehires");
        var leaver = ad.AddUser("adele.vance", recorded.DistinguishedName);
        var snapshot = Snapshot(onPrem: new SnapshotOnPrem
        {
            ObjectGuid = leaver.ObjectGuid, SamAccountName = "adele.vance", DistinguishedName = leaver.DistinguishedName, ParentDn = recorded.DistinguishedName, Enabled = true,
        });

        var preview = await Step("restore-ad-ou", ad).PreviewAsync(Context(snapshot, synced: true, ou: elsewhere.DistinguishedName), Ct);

        preview.Summary.Should().Be("Move adele.vance from Users to Rehires");
        preview.Reason.Should().Be("The OU you chose, not the one the snapshot recorded.");
    }

    // ---- Entra ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Groups_licences_and_attributes_come_back_and_synced_or_dynamic_groups_are_left_to_their_masters()
    {
        _graphHandler.Enqueue(HttpStatusCode.OK, Offboarded());
        var ctx = Context(Snapshot());
        var steps = Catalog();

        var groups = await steps.Single(s => s.Id == "restore-groups").PreviewAsync(ctx, Ct);
        var licences = await steps.Single(s => s.Id == "restore-licences").PreviewAsync(ctx, Ct);
        var attributes = await steps.Single(s => s.Id == "restore-attributes").PreviewAsync(ctx, Ct);
        var signIn = await steps.Single(s => s.Id == "restore-sign-in").PreviewAsync(ctx, Ct);

        _graphHandler.Requests.Should().ContainSingle("the steps share one live read");
        groups.Summary.Should().Be("Add back to 1 group");
        groups.Details.Should().Equal("Sales", "Finance (AD) — synced from AD; comes back with the AD groups");
        licences.Summary.Should().Be("Assign 1 licence: Microsoft 365 E3");
        attributes.Summary.Should().Be("Set 2 attributes back");
        attributes.Details.Should().Equal("department: (empty) → Sales", "employeeType: Offboarded → (empty)");
        signIn.Summary.Should().Be("Enable sign-in for Adele@contoso.com");
    }

    [Fact]
    public async Task A_synced_accounts_job_details_and_manager_are_left_to_ad()
    {
        var ctx = Context(Snapshot() with { Manager = new SnapshotRef("m-1", "Grace") }, synced: true);

        (await Step("restore-attributes").PreviewAsync(ctx, Ct)).IsNoOp.Should().BeTrue();
        (await Step("restore-manager").PreviewAsync(ctx, Ct)).IsNoOp.Should().BeTrue();
        _graphHandler.Requests.Should().BeEmpty();
    }

    // ---- Exchange ---------------------------------------------------------------------------------------------------

    private static string Mailbox(Action<JsonObject>? edit = null)
    {
        var doc = JsonNode.Parse(Fixtures.Text("exchange/mailbox"))!;
        edit?.Invoke(doc["value"]![0]!.AsObject());
        return doc.ToJsonString();
    }

    [Fact]
    public async Task The_mailbox_settings_put_back_are_exactly_the_ones_that_differ_and_the_hold_is_left_on()
    {
        var recorded = new SnapshotMailbox
        {
            RecipientType = "UserMailbox", HiddenFromAddressLists = false, AutoReplyState = "Disabled",
            FullAccess = ["Megan@contoso.com"], EnabledInboxRules = ["Falcon updates"],
        };
        var autoReplyOn = JsonNode.Parse(Fixtures.Text("exchange/auto-reply-configuration"))!;
        autoReplyOn["value"]![0]!["AutoReplyState"] = "Enabled";
        var rulesOff = JsonNode.Parse(Fixtures.Text("exchange/inbox-rules"))!;
        rulesOff["value"]![0]!["Enabled"] = false;
        _exo.Enqueue(HttpStatusCode.OK, Mailbox(r =>
            {
                r["RecipientTypeDetails"] = "SharedMailbox";
                r["HiddenFromAddressListsEnabled"] = true;
                r["LitigationHoldEnabled"] = true;
            }))
            .Enqueue(HttpStatusCode.OK, autoReplyOn.ToJsonString())
            .Enqueue(HttpStatusCode.OK, rulesOff.ToJsonString())
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mailbox-permissions-shared"));   // Adele and Megan

        var preview = await Step("restore-mailbox-settings").PreviewAsync(Context(Snapshot(mailbox: recorded)), Ct);

        preview.Summary.Should().Be("Put back 4 mailbox settings");
        preview.Details.Should().Equal(
            "Show in address lists again",
            "Turn off the automatic reply",
            "Turn the rule \"Falcon updates\" back on",
            "Remove Adele@contoso.com's Full Access, granted since the snapshot",
            "Litigation hold is left on: taking a mailbox off hold is a legal decision");
    }

    [Fact]
    public async Task Access_to_other_mailboxes_comes_back_from_the_runs_own_action_log_rows()
    {
        _actions.Record(new ActionRecord
        {
            Actor = "admin@contoso.com", TenantId = "t-1", TargetType = ObjectType.User, TargetId = "mbx-info", TargetName = "Contoso Info",
            Action = "exchange.sendas.remove", Outcome = ActionOutcome.Success, InverseAction = "exchange.sendas.add",
            InversePayload = """{"principal":"Adele@contoso.com"}""", CorrelationId = "run-1",
        });
        _actions.Record(new ActionRecord
        {
            Actor = "admin@contoso.com", TenantId = "t-1", TargetType = ObjectType.User, TargetId = "mbx-other", TargetName = "Other run",
            Action = "exchange.fullaccess.remove", Outcome = ActionOutcome.Success, InverseAction = "exchange.fullaccess.add",
            InversePayload = """{"principal":"Adele@contoso.com"}""", CorrelationId = "some-other-run",
        });

        _exo.Enqueue(HttpStatusCode.OK, """{"value":[]}""");   // Get-RecipientPermission on Contoso Info: not back yet

        var preview = await Step("restore-other-mailbox-access").PreviewAsync(Context(Snapshot()), Ct);

        preview.Summary.Should().Be("Give back 1 permission on other mailboxes");
        preview.Details.Should().Equal("Send As on Contoso Info");
    }

    [Fact]
    public async Task Access_that_is_already_back_is_not_offered_again()
    {
        // Found at the M15 gate: a second restore offered to give back what the first had already returned.
        _actions.Record(new ActionRecord
        {
            Actor = "admin@contoso.com", TenantId = "t-1", TargetType = ObjectType.User, TargetId = "mbx-info", TargetName = "Contoso Info",
            Action = "exchange.sendas.remove", Outcome = ActionOutcome.Success, InverseAction = "exchange.sendas.add",
            InversePayload = """{"principal":"Adele@contoso.com"}""", CorrelationId = "run-1",
        });
        _exo.Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/recipient-permissions-shared"));   // Adele can send as it again

        var preview = await Step("restore-other-mailbox-access").PreviewAsync(Context(Snapshot()), Ct);

        preview.IsNoOp.Should().BeTrue();
        preview.Summary.Should().Be("The 1 permission the run took away is back");
    }

    [Fact]
    public async Task A_shared_mailbox_that_was_a_user_mailbox_is_converted_back_after_the_licences()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox(r => r["RecipientTypeDetails"] = "SharedMailbox"));

        var preview = await Step("restore-mailbox-type").PreviewAsync(Context(Snapshot(mailbox: new SnapshotMailbox { RecipientType = "UserMailbox" })), Ct);

        preview.CanRun.Should().BeTrue();
        preview.Reason.Should().StartWith("Runs after the licences");
    }
}
