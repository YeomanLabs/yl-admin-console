using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Tests.Exchange;
using AdminConsole.Core.Tests.Fakes;
using AdminConsole.Core.Users;

namespace AdminConsole.Core.Tests.Offboarding;

/// <summary>
/// M15.1 — the before-picture. The property that matters is the halting one: if the snapshot cannot be taken or
/// written, nothing else in the run may happen, because an offboarding that cannot be reversed must not be the default.
/// </summary>
public sealed class SnapshotTests : IDisposable
{
    private const string UserId = "u-1";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeHttpHandler _graphHandler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ac-snapshot-tests", Guid.NewGuid().ToString("N"));
    private readonly UserService _users;

    public SnapshotTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var graph = new GraphClient(new HttpClient(_graphHandler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _users = new UserService(graph, _db, _actions, _tenant, NullLogger<UserService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SnapshotService Service() =>
        new(_users, _db, new FakeDirectoryService(), MailboxSummaryTests.Service(new FakeHttpHandler(), granted: false), NullLogger<SnapshotService>.Instance);

    private OffboardingContext Context() => new()
    {
        User = new DirectoryUser
        {
            Id = UserId, UserPrincipalName = "ada@contoso.com", DisplayName = "Ada Lovelace", AccountEnabled = true,
            OnPremisesSyncEnabled = false, Licensed = true, Raw = "{}",
        },
        Tenant = _tenant,
        CorrelationId = "run-1",
    };

    private static string Batch(params (string Id, int Status, string Body)[] inner) =>
        "{\"responses\":[" + string.Join(",", inner.Select(i => $"{{\"id\":\"{i.Id}\",\"status\":{i.Status},\"body\":{i.Body}}}")) + "]}";

    private const string Groups = """{"value":[{"@odata.type":"#microsoft.graph.group","id":"g-1","displayName":"Sales","groupTypes":[],"securityEnabled":true,"mailEnabled":false},{"@odata.type":"#microsoft.graph.group","id":"g-2","displayName":"Sales List","groupTypes":[],"securityEnabled":false,"mailEnabled":true}]}""";
    private const string Denied = """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""";

    private static string Hydrated(int memberOfStatus = 200) => Batch(
        ("user", 200, """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"department":"Engineering","jobTitle":"Analyst","usageLocation":"GB"}"""),
        ("manager", 200, """{"id":"m-1","displayName":"Grace Hopper","userPrincipalName":"grace@contoso.com"}"""),
        ("memberOf", memberOfStatus, memberOfStatus == 200 ? Groups : Denied),
        ("auth", 200, """{"value":[{"@odata.type":"#microsoft.graph.passwordAuthenticationMethod","id":"p"}]}"""),
        ("devices", 200, """{"value":[]}"""),
        ("licenses", 200, """{"id":"u-1","licenseAssignmentStates":[{"skuId":"sku-direct","assignedByGroup":null,"state":"Active"},{"skuId":"sku-group","assignedByGroup":"g-9","state":"Active"}]}"""),
        ("reports", 200, """{"value":[]}"""));

    [Fact]
    public async Task The_snapshot_records_what_restore_needs_and_names_what_it_could_not_read()
    {
        _graphHandler.Enqueue(HttpStatusCode.OK, Hydrated());

        var s = await Service().TakeAsync(Context(), Ct);

        s.Manager.Should().Be(new SnapshotRef("m-1", "Grace Hopper"));
        s.Groups.Select(g => (g.Id, g.MailEnabledList)).Should().Equal(("g-1", false), ("g-2", true));
        s.Licences.Select(l => l.Id).Should().Equal(["sku-direct"], "a group-assigned licence comes back with the group, not on its own");
        s.Attributes["department"].Should().Be("Engineering");
        s.Attributes["jobTitle"].Should().Be("Analyst");
        s.UsageLocation.Should().Be("GB");
        s.OnPrem.Should().BeNull("a cloud-only account has no AD side");
        s.Mailbox.Should().BeNull();
        s.Gaps.Should().ContainSingle().Which.Should().StartWith("Exchange:");
    }

    [Fact]
    public async Task A_snapshot_that_cannot_read_the_groups_stops_the_whole_run()
    {
        _graphHandler.Enqueue(HttpStatusCode.OK, Hydrated(memberOfStatus: 403));
        var step = new SnapshotStep(Service(), new SnapshotStore(_root), _actions);
        var later = new RecordingStep("disable-account");
        var runner = new OffboardingRunner([step, later], NullLogger<OffboardingRunner>.Instance);
        var ctx = Context();

        var previews = await runner.PreviewAsync(ctx, [step.Id, later.Id], Ct);
        previews[0].Failed.Should().BeTrue();
        previews[0].Reason.Should().StartWith("The account's groups and licences could not be read");

        var run = await runner.ExecuteAsync(ctx, previews, null, Ct);

        run.Halted.Should().BeTrue();
        run.Results[0].Outcome.Should().Be(StepOutcome.Failed);
        run.Results[1].Outcome.Should().Be(StepOutcome.NotRun);
        later.Executed.Should().BeFalse("nothing may change once the before-picture is missing");
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task Executing_saves_the_previewed_snapshot_and_logs_where_it_went()
    {
        _graphHandler.Enqueue(HttpStatusCode.OK, Hydrated());
        var store = new SnapshotStore(_root);
        var step = new SnapshotStep(Service(), store, _actions);
        var ctx = Context();

        (await step.PreviewAsync(ctx, Ct)).Summary.Should().StartWith("Record 2 groups, 1 direct licence, the manager");
        var result = await step.ExecuteAsync(ctx, Ct);

        result.Outcome.Should().Be(StepOutcome.Success);
        _graphHandler.Requests.Should().ContainSingle("the execute saves what the preview read rather than reading again");
        var saved = store.List("t-1", UserId).Should().ContainSingle().Subject;
        saved.Snapshot.Groups.Should().HaveCount(2);
        _actions.Query(new ActionLogQuery { ActionPrefix = "offboarding.snapshot" }).Should().ContainSingle()
            .Which.RequestSummary.Should().Be(saved.Path);
        Directory.EnumerateFiles(Path.Combine(_root, "t-1"), "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Snapshots_list_newest_first_and_a_damaged_file_is_skipped()
    {
        var store = new SnapshotStore(_root);
        OffboardingSnapshot At(int hour) => new() { TenantId = "t-1", UserId = UserId, UserPrincipalName = "ada@contoso.com", TakenAt = new DateTimeOffset(2026, 10, 17, hour, 0, 0, TimeSpan.Zero) };
        store.Save(At(9));
        store.Save(At(11));
        File.WriteAllText(Path.Combine(_root, "t-1", $"{UserId}_broken.json"), "{ not json");

        store.List("t-1", UserId).Select(s => s.Snapshot.TakenAt.Hour).Should().Equal(11, 9);
    }

    private sealed class RecordingStep(string id) : IOffboardingStep
    {
        public bool Executed { get; private set; }
        public string Id => id;
        public string Title => id;
        public StepSurface Surface => StepSurface.Graph;
        public Capability? RequiredCapability => null;
        public bool DefaultEnabled => true;
        public bool HaltsOnFailure => false;

        public Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct) =>
            Task.FromResult(new StepPreview { StepId = id, Summary = "would run" });

        public Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
        {
            Executed = true;
            return Task.FromResult(StepResult.Success(id, "ran"));
        }
    }
}
