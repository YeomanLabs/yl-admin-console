using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Templates;
using AdminConsole.Core.Tests.Exchange;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Templates;

/// <summary>
/// M17 — hybrid onboarding. The account is made in AD; its cloud half waits for Entra Connect and runs when the account
/// first appears in the cache by its source anchor; its mailbox half waits for the mailbox. Nothing here may lose the
/// second half to a restart, or run it against the wrong account.
/// </summary>
public sealed class HybridProvisioningTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeHttpHandler _http = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly FakeDirectoryService _ad = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ac-pending-tests", Guid.NewGuid().ToString("N"));
    private readonly OnPremOu _sync;
    private readonly OnPremGroup _adGroup;

    public HybridProvisioningTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var lab = _ad.AddOu("Lab");
        _sync = _ad.AddOu("CloudSync", lab.DistinguishedName);
        _adGroup = _ad.AddGroup("VPN Users", lab.DistinguishedName);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private GraphClient Graph() => new(new HttpClient(_http), new FakeAuthService(), NullLogger<GraphClient>.Instance);

    private PendingProvisioningService Pending() => new(new PendingProvisioningStore(_dir), _db,
        new UserProvisioningService(Graph(), _db, _actions, _tenant, NullLogger<UserProvisioningService>.Instance, (_, _) => Task.CompletedTask),
        MailboxSummaryTests.Service(_http, granted: false), Graph(), _tenant, _actions, NullLogger<PendingProvisioningService>.Instance);

    private HybridProvisioningService Hybrid(PendingProvisioningService pending) =>
        new(_ad, pending, _db, _actions, _tenant, NullLogger<HybridProvisioningService>.Instance);

    private UserTemplate Template(TemplateExchange? exchange = null) => new()
    {
        Name = "Field engineer",
        OnPrem = new TemplateOnPrem
        {
            OuDn = _sync.DistinguishedName,
            Groups = [new TemplateAdGroup(_adGroup.DistinguishedName, "VPN Users")],
            Attributes = new Dictionary<string, string?> { ["department"] = "Field", ["title"] = "Engineer" },
            AddressRules = ["{alias}@contoso.co.uk"],
        },
        Exchange = exchange,
    };

    private static CreateUserRequest Request(string password = "Correct-Horse-9-Battery") => new()
    {
        DisplayName = "Ada Lovelace",
        GivenName = "Ada",
        Surname = "Lovelace",
        UserPrincipalName = "ada.lovelace@contoso.com",
        MailNickname = "ada.lovelace",
        Attributes = new Dictionary<string, object?> { ["usageLocation"] = "GB", ["mail"] = "ada.lovelace@contoso.com" },
        SkuIds = ["sku-e3"],
        Groups = [new TemplateGroup { Id = "g-cloud", DisplayName = "Sales" }],
        Password = password,
    };

    // ---- AD create -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_AD_account_is_created_enabled_with_a_password_that_must_be_changed()
    {
        var created = await _ad.CreateUserAsync(new NewAdUser
        {
            ParentOuDn = _sync.DistinguishedName, Name = "Ada Lovelace", GivenName = "Ada", Surname = "Lovelace",
            SamAccountName = "ada.lovelace", UserPrincipalName = "ada.lovelace@adlab.test", Password = "Correct-Horse-9-Battery",
        }, Ct);

        created.Outcome.Success.Should().BeTrue();
        created.User!.Enabled.Should().BeTrue();
        created.User.PasswordLastSet.Should().NotBeNull();
        DistinguishedNames.Parent(created.User.DistinguishedName).Should().Be(_sync.DistinguishedName);
    }

    [Fact]
    public async Task A_password_AD_refuses_leaves_no_half_made_account_behind()
    {
        var created = await _ad.CreateUserAsync(new NewAdUser
        {
            ParentOuDn = _sync.DistinguishedName, Name = "Ada Lovelace", GivenName = "Ada", Surname = "Lovelace",
            SamAccountName = "ada.lovelace", UserPrincipalName = "ada.lovelace@adlab.test", Password = "short",
        }, Ct);

        created.Outcome.Success.Should().BeFalse();
        created.Outcome.Summary.Should().StartWith("Not created:");
        (await _ad.SearchUsersAsync("ada", 10, null, Ct)).Should().BeEmpty();
    }

    // ---- hybrid create -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_hybrid_create_makes_the_AD_half_and_queues_the_cloud_half()
    {
        _db.Write(w => w.UpsertGroup(new DirectoryGroup { Id = "g-synced", DisplayName = "Finance (AD)", Kind = GroupKind.Security, IsDynamic = false, OnPremisesSyncEnabled = true, IsAssignableToRole = false, Raw = "{}" }));
        var pending = Pending();
        var request = Request() with { Groups = [new TemplateGroup { Id = "g-cloud", DisplayName = "Sales" }, new TemplateGroup { Id = "g-synced", DisplayName = "Finance (AD)" }] };

        var result = await Hybrid(pending).CreateAsync(Template(), request, _sync.DistinguishedName, "ada.lovelace", "ada.lovelace@adlab.test", Ct);

        result.Success.Should().BeTrue();
        result.Password.Should().Be("Correct-Horse-9-Battery", "shown once, from the result");
        var user = (await _ad.SearchUsersAsync("ada", 10, null, Ct)).Single();
        user.Department.Should().Be("Field");
        user.MemberOfDns.Should().Equal(_adGroup.DistinguishedName);
        _ad.ProxyAddressesOf[user.ObjectGuid].Should().Equal("SMTP:ada.lovelace@contoso.com", "smtp:ada.lovelace@contoso.co.uk");

        var item = pending.Items.Should().ContainSingle().Subject;
        item.State.Should().Be(PendingState.WaitingForSync);
        item.ImmutableId.Should().Be(Convert.ToBase64String(user.ObjectGuid.ToByteArray()));
        item.Groups.Select(g => g.Id).Should().Equal(["g-cloud"], "a synced group comes with AD");
        result.Steps.Last().Title.Should().Be("Wait for Entra Connect");
    }

    [Fact]
    public async Task A_cloud_only_manager_is_said_to_be_impossible_for_a_synced_user_rather_than_failing_later()
    {
        _db.Write(w => w.UpsertUser(DirectoryUser.FromJson(Fixtures.Parse("""{"id":"m-1","displayName":"Grace Hopper","userPrincipalName":"grace@contoso.com","accountEnabled":true,"assignedLicenses":[]}"""))));

        var result = await Hybrid(Pending()).CreateAsync(Template(), Request() with { ManagerId = "m-1" }, _sync.DistinguishedName, "ada.lovelace", "ada.lovelace@adlab.test", Ct);

        result.Steps.Single(s => s.Title == "Set the manager in AD").Message.Should().StartWith("Grace Hopper has no AD account");
    }

    // ---- the queue -----------------------------------------------------------------------------------------------

    private PendingProvisioning Queued(Guid adGuid, TemplateExchange? exchange = null) => new()
    {
        Id = "p-1", TenantId = "t-1", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20), DisplayName = "Ada Lovelace",
        AdUserPrincipalName = "ada.lovelace@adlab.test", SamAccountName = "ada.lovelace", AdGuid = adGuid,
        UsageLocation = "GB", SkuIds = ["sku-e3"], Groups = [new TemplateGroup { Id = "g-cloud", DisplayName = "Sales" }], Exchange = exchange,
    };

    private const string Org = """{"value":[{"onPremisesLastSyncDateTime":"2026-10-17T14:27:52Z"}]}""";

    [Fact]
    public async Task Before_Entra_Connect_runs_nothing_happens_and_the_next_cycle_is_known()
    {
        var pending = Pending();
        pending.Enqueue(Queued(Guid.NewGuid()));
        _http.Enqueue(HttpStatusCode.OK, Org);

        await pending.ProcessAsync(Ct);

        pending.Items.Single().State.Should().Be(PendingState.WaitingForSync);
        pending.NextSyncDue.Should().Be(new DateTimeOffset(2026, 10, 17, 14, 57, 52, TimeSpan.Zero));
        _http.Requests.Should().ContainSingle("only the last-sync read");
    }

    [Fact]
    public async Task When_the_account_appears_by_its_anchor_the_cloud_half_runs_and_it_finishes()
    {
        var guid = Guid.NewGuid();
        var anchor = Convert.ToBase64String(guid.ToByteArray());
        _db.Write(w => w.UpsertUser(DirectoryUser.FromJson(Fixtures.Parse(
            $$"""{"id":"u-ada","displayName":"Ada Lovelace","userPrincipalName":"ada.lovelace@contoso.onmicrosoft.com","accountEnabled":true,"onPremisesSyncEnabled":true,"onPremisesImmutableId":"{{anchor}}","assignedLicenses":[]}"""))));
        var pending = Pending();
        pending.Enqueue(Queued(guid));
        _http.Enqueue(HttpStatusCode.OK, Org)
            .Enqueue(HttpStatusCode.NoContent)                       // PATCH usageLocation
            .Enqueue(HttpStatusCode.OK, """{"id":"u-ada"}""")         // assignLicense
            .Enqueue(HttpStatusCode.OK, """{"responses":[{"id":"g-cloud","status":204,"body":{}}]}""");

        await pending.ProcessAsync(Ct);

        var item = pending.Items.Single();
        item.State.Should().Be(PendingState.Done, "the template has no mailbox half");
        item.CloudUserId.Should().Be("u-ada");
        item.Licensed.Should().BeTrue();
        item.HasProblems.Should().BeFalse();
        item.WaitedForSync.Should().BeGreaterThan(TimeSpan.FromMinutes(19));
        _http.Requests[1].Body.Should().Contain("\"usageLocation\":\"GB\"");
        _actions.Query(new ActionLogQuery { ActionPrefix = "provision.cloud" }).Should().ContainSingle();
    }

    [Fact]
    public async Task Without_a_licence_the_mailbox_half_says_why_it_never_ran_instead_of_waiting_forever()
    {
        var guid = Guid.NewGuid();
        var anchor = Convert.ToBase64String(guid.ToByteArray());
        _db.Write(w => w.UpsertUser(DirectoryUser.FromJson(Fixtures.Parse(
            $$"""{"id":"u-ada","displayName":"Ada","userPrincipalName":"ada@contoso.onmicrosoft.com","accountEnabled":true,"onPremisesSyncEnabled":true,"onPremisesImmutableId":"{{anchor}}","assignedLicenses":[]}"""))));
        var pending = Pending();
        pending.Enqueue(Queued(guid, new TemplateExchange { Language = "en-GB", TimeZone = "GMT Standard Time" }) with { Groups = [] });
        _http.Enqueue(HttpStatusCode.OK, Org)
            .Enqueue(HttpStatusCode.NoContent)
            .Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"Request_BadRequest","message":"License assignment failed because service plans … no available licenses."}}""");

        await pending.ProcessAsync(Ct);

        var item = pending.Items.Single();
        item.State.Should().Be(PendingState.Done);
        item.HasProblems.Should().BeTrue();
        item.Notes.Last().Text.Should().StartWith("The mailbox settings were not applied: without a licence");
    }

    [Fact]
    public async Task The_queue_survives_a_restart_and_an_abandoned_item_stays_abandoned()
    {
        var first = Pending();
        first.Enqueue(Queued(Guid.NewGuid()));

        var second = Pending();
        second.Items.Should().ContainSingle().Which.State.Should().Be(PendingState.WaitingForSync);
        second.Abandon("p-1");

        Pending().Items.Single().State.Should().Be(PendingState.Abandoned);
        Pending().Open.Should().BeEmpty();
    }

    [Fact]
    public void The_pre_Windows_2000_name_follows_the_rule_and_stops_at_twenty_characters()
    {
        HybridProvisioningService.SamFor(Template(), "Maximiliana", "Featherstonehaugh").Should().Be("maximiliana.feathers");
    }
}
