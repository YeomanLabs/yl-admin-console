using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.Tests.Exchange;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Offboarding;

/// <summary>
/// M15.3 — the mailbox half of an offboarding. What matters most here is what each step does <em>not</em> touch: rules
/// that only file mail, lists synced from AD, settings AD masters, and a delegate nobody chose.
/// </summary>
public sealed class ExchangeOffboardingStepTests : IDisposable
{
    private const string UserId = "00000000-0000-0000-0000-000000000104";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly PersonRef Megan = new("00000000-0000-0000-0000-000000000107", "Megan Bowen", "Megan@contoso.com");

    private readonly FakeHttpHandler _exo = new();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly CacheDb _db = CacheDb.OpenInMemory();

    public void Dispose()
    {
        _actions.Dispose();
        _db.Dispose();
    }

    private ExchangeService Exchange(bool granted = true) => MailboxSummaryTests.Service(_exo, granted, _actions);

    private static OffboardingContext Context(bool synced = false, PersonRef? delegateTo = null, string? autoReply = null) => new()
    {
        User = new DirectoryUser
        {
            Id = UserId,
            UserPrincipalName = "Adele@contoso.com",
            DisplayName = "Adele Vance",
            AccountEnabled = true,
            OnPremisesSyncEnabled = synced,
            Licensed = true,
            Raw = "{}",
        },
        Tenant = new TenantContext(),
        Delegate = delegateTo,
        CorrelationId = "run-1",
        AutoReplyMessage = autoReply,
    };

    private static string Mailbox(Action<JsonObject>? edit = null)
    {
        var doc = JsonNode.Parse(Fixtures.Text("exchange/mailbox"))!;
        edit?.Invoke(doc["value"]![0]!.AsObject());
        return doc.ToJsonString();
    }

    private static JsonDocument Body(FakeHttpHandler.RecordedRequest r) => JsonDocument.Parse(r.Body!);

    private static string Cmdlet(FakeHttpHandler.RecordedRequest r)
    {
        using var body = Body(r);
        return body.RootElement.GetProperty("CmdletInput").GetProperty("CmdletName").GetString()!;
    }

    // ---- what every Exchange step shares ----------------------------------------------------------------------------

    [Fact]
    public async Task Without_the_exchange_permission_every_step_is_blocked_with_the_way_to_grant_it_and_nothing_is_sent()
    {
        var exchange = Exchange(granted: false);
        IOffboardingStep[] steps = [new HideFromGalStep(exchange), new ConvertToSharedStep(exchange), new RemoveInboxRulesStep(exchange)];

        foreach (var step in steps)
        {
            var preview = await step.PreviewAsync(Context(), Ct);
            preview.CanRun.Should().BeFalse();
            preview.Reason.Should().Be(ExchangeStep.NotGranted);
        }

        _exo.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_user_with_no_mailbox_is_nothing_to_do_rather_than_a_failure()
    {
        _exo.Enqueue(HttpStatusCode.NotFound, Fixtures.Text("exchange/error-not-found"));

        var preview = await new HideFromGalStep(Exchange()).PreviewAsync(Context(), Ct);

        preview.IsNoOp.Should().BeTrue();
        preview.Summary.Should().Be("Adele Vance has no Exchange Online mailbox");
    }

    [Fact]
    public async Task Steps_previewing_at_once_share_one_mailbox_read()
    {
        // Eleven steps preview in parallel; eleven identical Get-Mailbox calls would be the slowest part of the dialog.
        _exo.Enqueue(HttpStatusCode.OK, Mailbox());
        var exchange = Exchange();
        var ctx = Context();

        var previews = await Task.WhenAll(new HideFromGalStep(exchange).PreviewAsync(ctx, Ct), new LitigationHoldStep(exchange).PreviewAsync(ctx, Ct));

        previews.Should().OnlyContain(p => p.CanRun);
        _exo.Requests.Should().ContainSingle().Which.Body.Should().Contain("\"Get-Mailbox\"");
    }

    [Fact]
    public async Task On_a_synced_mailbox_the_settings_ad_masters_are_blocked_and_the_rest_still_run()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox(r => r["IsDirSynced"] = true));
        var exchange = Exchange();
        var ctx = Context(synced: true, delegateTo: Megan);

        var hide = await new HideFromGalStep(exchange).PreviewAsync(ctx, Ct);
        var forward = await new ForwardMailStep(exchange).PreviewAsync(ctx, Ct);
        var hold = await new LitigationHoldStep(exchange).PreviewAsync(ctx, Ct);

        hide.CanRun.Should().BeFalse();
        hide.Reason.Should().Be(ExchangeStep.SyncedReason);
        forward.Reason.Should().Be(ExchangeStep.SyncedReason);
        hold.CanRun.Should().BeTrue("a hold is an Exchange setting even on a synced mailbox");
    }

    [Fact]
    public async Task A_room_mailbox_is_left_alone()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox(r => r["RecipientTypeDetails"] = "RoomMailbox"));

        var preview = await new ConvertToSharedStep(Exchange()).PreviewAsync(Context(), Ct);

        preview.IsNoOp.Should().BeTrue();
        preview.Summary.Should().Contain("room");
    }

    // ---- hide, convert, hold ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Hide_writes_the_flag_and_confirms_it_from_two_reads()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox())
            .Enqueue(HttpStatusCode.OK, Mailbox())
            .Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true))
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true));
        var step = new HideFromGalStep(Exchange());
        var ctx = Context();

        (await step.PreviewAsync(ctx, Ct)).Summary.Should().Be("Hide Adele@contoso.com from the address book; mail sent to it still arrives");
        var result = await step.ExecuteAsync(ctx, Ct);

        result.Outcome.Should().Be(StepOutcome.Success);
        result.CanUndo.Should().BeTrue();
        using var set = Body(_exo.Requests[2]);
        set.RootElement.GetProperty("CmdletInput").GetProperty("Parameters").GetProperty("HiddenFromAddressListsEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Already_hidden_is_a_no_op()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true));

        (await new HideFromGalStep(Exchange()).PreviewAsync(Context(), Ct)).IsNoOp.Should().BeTrue();
    }

    [Fact]
    public async Task Convert_says_why_it_runs_before_the_licence_step()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mailbox-statistics"));

        var preview = await new ConvertToSharedStep(Exchange()).PreviewAsync(Context(), Ct);

        preview.CanRun.Should().BeTrue();
        preview.Summary.Should().Be("Convert Adele@contoso.com to a shared mailbox (82.63 MB)");
        preview.Reason.Should().StartWith("Runs before the licence step");
    }

    [Fact]
    public async Task Convert_warns_when_the_shared_mailbox_will_still_need_a_licence()
    {
        var big = JsonNode.Parse(Fixtures.Text("exchange/mailbox-statistics"))!;
        big["value"]![0]!["TotalItemSize"] = "61.5 GB (66,035,113,984 bytes)";
        _exo.Enqueue(HttpStatusCode.OK, Mailbox(r =>
            {
                r["ArchiveStatus"] = "Active";
                r["LitigationHoldEnabled"] = true;
            }))
            .Enqueue(HttpStatusCode.OK, big.ToJsonString());

        var preview = await new ConvertToSharedStep(Exchange()).PreviewAsync(Context(), Ct);

        preview.Reason.Should().Contain("over 50 GB still needs a licence").And.Contain("has an archive").And.Contain("litigation hold");
    }

    [Fact]
    public async Task Already_shared_is_a_no_op()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox(r => r["RecipientTypeDetails"] = "SharedMailbox"));

        (await new ConvertToSharedStep(Exchange()).PreviewAsync(Context(), Ct)).IsNoOp.Should().BeTrue();
    }

    // ---- auto-reply, forwarding, delegation -------------------------------------------------------------------------

    [Fact]
    public async Task The_auto_reply_template_names_the_delegate_and_the_preview_shows_it_word_for_word()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/auto-reply-configuration"));

        var preview = await new SetAutoReplyStep(Exchange()).PreviewAsync(Context(delegateTo: Megan), Ct);

        preview.Details.Should().Equal("“Thank you for your message. Adele Vance is no longer with us; please contact Megan Bowen (Megan@contoso.com) instead.”");
    }

    [Fact]
    public async Task The_auto_reply_is_sent_as_encoded_html_to_everyone()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox())
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/auto-reply-configuration"))   // preview
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/auto-reply-configuration"))   // before
            .Enqueue(HttpStatusCode.OK);
        var on = JsonNode.Parse(Fixtures.Text("exchange/auto-reply-configuration"))!;
        on["value"]![0]!["AutoReplyState"] = "Enabled";
        _exo.Enqueue(HttpStatusCode.OK, on.ToJsonString()).Enqueue(HttpStatusCode.OK, on.ToJsonString());
        var step = new SetAutoReplyStep(Exchange());
        var ctx = Context(autoReply: "Gone fishing <back never>");

        await step.PreviewAsync(ctx, Ct);
        var result = await step.ExecuteAsync(ctx, Ct);

        result.Outcome.Should().Be(StepOutcome.Success);
        using var set = Body(_exo.Requests[3]);
        var p = set.RootElement.GetProperty("CmdletInput").GetProperty("Parameters");
        p.GetProperty("InternalMessage").GetString().Should().Be("Gone fishing &lt;back never&gt;");
        p.GetProperty("ExternalAudience").GetString().Should().Be("All");
    }

    [Fact]
    public async Task A_reply_of_several_lines_keeps_its_line_breaks()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox())
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/auto-reply-configuration"))   // preview
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/auto-reply-configuration"))   // before
            .Enqueue(HttpStatusCode.OK);
        var on = JsonNode.Parse(Fixtures.Text("exchange/auto-reply-configuration"))!;
        on["value"]![0]!["AutoReplyState"] = "Enabled";
        _exo.Enqueue(HttpStatusCode.OK, on.ToJsonString()).Enqueue(HttpStatusCode.OK, on.ToJsonString());
        var step = new SetAutoReplyStep(Exchange());
        var ctx = Context(autoReply: "Adele has left.\r\nPlease write to Megan & Co.");

        await step.PreviewAsync(ctx, Ct);
        await step.ExecuteAsync(ctx, Ct);

        using var set = Body(_exo.Requests[3]);
        set.RootElement.GetProperty("CmdletInput").GetProperty("Parameters").GetProperty("InternalMessage").GetString()
            .Should().Be("Adele has left.<br>Please write to Megan &amp; Co.");
    }

    [Fact]
    public async Task A_template_reply_fills_in_its_placeholders_and_the_preview_shows_the_result()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/auto-reply-configuration"));

        var preview = await new SetAutoReplyStep(Exchange()).PreviewAsync(
            Context(delegateTo: Megan, autoReply: "  {firstName} has left {company}. Please write to {delegate} at {delegateEmail}.  "), Ct);

        preview.CanRun.Should().BeTrue();
        preview.Details.Should().Equal("“Adele has left our organisation. Please write to Megan Bowen at Megan@contoso.com.”");
    }

    [Fact]
    public async Task A_template_reply_that_names_the_delegate_waits_for_one_to_be_chosen()
    {
        // Sent as written it would tell customers to contact "{delegate}".
        _exo.Enqueue(HttpStatusCode.OK, Mailbox());

        var preview = await new SetAutoReplyStep(Exchange()).PreviewAsync(Context(autoReply: "Please write to {delegate}."), Ct);

        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().Be("This template's automatic reply names the delegate. Choose a delegate, or untick this step.");
    }

    [Fact]
    public async Task Forwarding_with_no_delegate_is_blocked_unless_mail_already_leaves_the_organisation()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox());
        var blocked = await new ForwardMailStep(Exchange()).PreviewAsync(Context(), Ct);
        blocked.CanRun.Should().BeFalse();
        blocked.Reason.Should().StartWith("Choose a delegate first");

        _exo.Enqueue(HttpStatusCode.OK, Mailbox(r => r["ForwardingSmtpAddress"] = "smtp:adele.personal@gmail.example"));
        var stop = await new ForwardMailStep(Exchange()).PreviewAsync(Context(), Ct);
        stop.CanRun.Should().BeTrue();
        stop.Summary.Should().Be("Stop forwarding to adele.personal@gmail.example, outside the organisation");
    }

    [Fact]
    public async Task Forwarding_to_the_delegate_says_what_it_replaces()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox(r => r["ForwardingSmtpAddress"] = "smtp:adele.personal@gmail.example"));

        var preview = await new ForwardMailStep(Exchange()).PreviewAsync(Context(delegateTo: Megan), Ct);

        preview.Summary.Should().Be("Forward new mail to Megan Bowen, keeping a copy in the mailbox");
        preview.Reason.Should().Be("It forwards to adele.personal@gmail.example, outside the organisation now; this replaces that.");
    }

    [Fact]
    public async Task Delegation_needs_a_delegate_and_is_a_no_op_when_they_already_have_access()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox());
        (await new DelegateMailboxStep(Exchange()).PreviewAsync(Context(), Ct)).Reason.Should().StartWith("Choose a delegate first");

        _exo.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mailbox-permissions-shared"));
        (await new DelegateMailboxStep(Exchange()).PreviewAsync(Context(delegateTo: Megan), Ct)).IsNoOp.Should().BeTrue();
    }

    // ---- rules, devices, meetings -----------------------------------------------------------------------------------

    [Fact]
    public async Task Only_rules_that_send_mail_elsewhere_are_turned_off_and_undo_turns_them_back_on()
    {
        var rules = JsonNode.Parse(Fixtures.Text("exchange/inbox-rules"))!;
        var filing = rules["value"]![0]!.DeepClone().AsObject();
        filing["Name"] = "File newsletters";
        filing["RuleIdentity"] = "42";
        filing["ForwardTo"] = new JsonArray();
        rules["value"]!.AsArray().Add(filing);
        var off = JsonNode.Parse(rules.ToJsonString())!;
        off["value"]![0]!["Enabled"] = false;

        _exo.Enqueue(HttpStatusCode.OK, Mailbox())
            .Enqueue(HttpStatusCode.OK, rules.ToJsonString())
            .Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, off.ToJsonString())
            .Enqueue(HttpStatusCode.OK, off.ToJsonString());
        var step = new RemoveInboxRulesStep(Exchange());
        var ctx = Context();

        var preview = await step.PreviewAsync(ctx, Ct);
        preview.Summary.Should().Be("Turn off 1 inbox rule that sends mail elsewhere");
        preview.Details.Should().Equal("Falcon updates", "File newsletters — left on: it doesn't send mail anywhere");

        var result = await step.ExecuteAsync(ctx, Ct);
        result.Outcome.Should().Be(StepOutcome.Success);
        result.Message.Should().Be("Turned off 1 inbox rule");
        Cmdlet(_exo.Requests[2]).Should().Be("Disable-InboxRule");
        using (var body = Body(_exo.Requests[2]))
        {
            body.RootElement.GetProperty("CmdletInput").GetProperty("Parameters").GetProperty("Identity").GetString()
                .Should().Be($"{UserId}\\8821156604511518721");
        }

        result.CanUndo.Should().BeTrue();
    }

    [Fact]
    public async Task Phones_are_listed_by_name_and_removing_them_is_said_to_be_permanent()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mobile-devices"));

        var preview = await new RemoveMobileDevicesStep(Exchange()).PreviewAsync(Context(), Ct);

        preview.Summary.Should().Be("Remove 2 devices from the mailbox");
        preview.Reason.Should().Contain("Not undoable");
    }

    [Fact]
    public async Task Meetings_are_off_by_default_and_previewed_without_changing_anything()
    {
        _exo.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        var step = new CancelOrganizedMeetingsStep(Exchange());

        step.DefaultEnabled.Should().BeFalse("attendees are told, and that cannot be unsent");
        (await step.PreviewAsync(Context(), Ct)).Summary.Should().Be("Organises no meetings from today on");
        using var body = Body(_exo.Requests[1]);
        body.RootElement.GetProperty("CmdletInput").GetProperty("Parameters").GetProperty("PreviewOnly").GetBoolean().Should().BeTrue();
    }

    // ---- distribution lists and other mailboxes ---------------------------------------------------------------------

    [Fact]
    public async Task Distribution_lists_are_removed_here_and_lists_synced_from_ad_are_left_to_the_ad_step()
    {
        var graphHandler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"value":[
              {"@odata.type":"#microsoft.graph.group","id":"dl-1","displayName":"Sales Team","groupTypes":[],"mailEnabled":true,"securityEnabled":false},
              {"@odata.type":"#microsoft.graph.group","id":"dl-2","displayName":"Synced List","groupTypes":[],"mailEnabled":true,"securityEnabled":false,"onPremisesSyncEnabled":true},
              {"@odata.type":"#microsoft.graph.group","id":"sg-1","displayName":"All Staff","groupTypes":[],"mailEnabled":false,"securityEnabled":true},
              {"@odata.type":"#microsoft.graph.group","id":"m365-1","displayName":"Project X","groupTypes":["Unified"],"mailEnabled":true,"securityEnabled":false}
            ]}
            """);
        var graph = new GraphClient(new HttpClient(graphHandler), new FakeAuthService(), NullLogger<GraphClient>.Instance);

        var preview = await new RemoveFromDistributionListsStep(Exchange(), graph).PreviewAsync(Context(), Ct);

        preview.Summary.Should().Be("Remove from 1 distribution list");
        preview.Details.Should().Equal("Sales Team", "Synced List — synced from AD; the AD group step removes it");
    }

    [Fact]
    public async Task Access_to_other_mailboxes_is_found_by_send_as_tenant_wide_and_full_access_on_cached_shared_mailboxes()
    {
        _db.Write(w => w.UpsertMailbox(new MailboxSummary { UserId = "00000000-0000-0000-0000-000000000115", RecipientType = MailboxKinds.SharedMailbox, PrimarySmtp = "info@contoso.com" }));
        var resolved = """
            {"value":[{"Name":"Contoso Info","DisplayName":"Contoso Info","PrimarySmtpAddress":"info@contoso.com","ExternalDirectoryObjectId":"00000000-0000-0000-0000-000000000115","RecipientTypeDetails":"SharedMailbox"}]}
            """;
        _exo.Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/recipient-permissions-shared"))   // Get-RecipientPermission -Trustee
            .Enqueue(HttpStatusCode.OK, resolved)                                                  // Get-Recipient
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mailbox-permissions-shared"));     // Get-MailboxPermission on info@

        var preview = await new RemoveMailboxPermissionsStep(Exchange(), _db).PreviewAsync(Context(), Ct);

        preview.Summary.Should().Be("Remove 2 permissions on other mailboxes");
        preview.Details.Should().Equal("Send As on Contoso Info (info@contoso.com)", "Full Access on info@contoso.com");
        using var first = Body(_exo.Requests[0]);
        first.RootElement.GetProperty("CmdletInput").GetProperty("Parameters").GetProperty("Trustee").GetString().Should().Be("Adele@contoso.com");
    }

    // ---- how several writes become one step -------------------------------------------------------------------------

    [Fact]
    public void A_partial_failure_names_what_failed_and_keeps_the_undo_for_what_landed()
    {
        var undone = 0;
        var ok = new WriteOutcomeBuilder("Removed from Sales", () => undone++).Build();
        var bad = new AdminConsole.Core.Writes.WriteOutcome { Success = false, Summary = "Remove from Board", FriendlyError = "You don't manage this list" };

        var result = ExchangeStep.Combined("x", n => $"Removed from {n} lists", "lists", [("Sales", ok), ("Board", bad)]);

        result.Outcome.Should().Be(StepOutcome.Failed);
        result.Message.Should().Be("Removed from 1 lists, but 1 failed: Board: You don't manage this list");
        result.Undo.Should().NotBeNull();
    }

    private sealed record WriteOutcomeBuilder(string Summary, Action OnUndo)
    {
        public AdminConsole.Core.Writes.WriteOutcome Build() => new()
        {
            Success = true,
            Summary = Summary,
            Undo = _ =>
            {
                OnUndo();
                return Task.FromResult(new AdminConsole.Core.Writes.WriteOutcome { Success = true, Summary = "back" });
            },
        };
    }
}
