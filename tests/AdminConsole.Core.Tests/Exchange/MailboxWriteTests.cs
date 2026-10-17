using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Exchange;

/// <summary>
/// M14 — mailbox writes. Each reads first (Exchange reports no no-ops), writes routed by the target, logs its inverse,
/// and confirms from a read-back, because a 200 from Exchange arrives before the change is everywhere (doc 13 §5).
/// </summary>
public sealed class MailboxWriteTests
{
    private const string UserId = "00000000-0000-0000-0000-000000000101";
    private static readonly MailboxRef Target = new(UserId, "Adele Vance");

    private readonly FakeHttpHandler _handler = new();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private ExchangeService Sut => MailboxSummaryTests.Service(_handler, actions: _actions);

    /// <summary>A real Get-Mailbox response, edited.</summary>
    private static string Mailbox(Action<JsonObject>? edit = null)
    {
        var doc = JsonNode.Parse(Fixtures.Text("exchange/mailbox"))!;
        var row = doc["value"]![0]!.AsObject();
        edit?.Invoke(row);
        return doc.ToJsonString();
    }

    private static JsonArray Addresses(params string[] a) => new(a.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    [Fact]
    public async Task AddAddress_ReadsWritesConfirms_AndLogsTheInverse()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, Mailbox())                    // read: not there yet
            .Enqueue(HttpStatusCode.OK)                               // Set-Mailbox
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["EmailAddresses"] = Addresses("SMTP:Adele@contoso.com", "smtp:sales@contoso.com")))
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["EmailAddresses"] = Addresses("SMTP:Adele@contoso.com", "smtp:sales@contoso.com")));

        var outcome = await Sut.AddAddressAsync(Target, "sales@contoso.com", CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.CanUndo.Should().BeTrue();
        outcome.Summary.Should().Be("Added sales@contoso.com to Adele Vance");
        using (var body = JsonDocument.Parse(_handler.Requests[1].Body!))
        {
            var p = body.RootElement.GetProperty("CmdletInput").GetProperty("Parameters");
            p.GetProperty("Identity").GetString().Should().Be(UserId);
            p.GetProperty("EmailAddresses").GetProperty("add")[0].GetString().Should().Be("smtp:sales@contoso.com");
        }

        _handler.Requests.Should().OnlyContain(r => r.Headers["X-AnchorMailbox"] == $"OID:{UserId}@t-1");
        var row = _actions.Get(outcome.ActionId)!;
        row.Action.Should().Be("exchange.address.add");
        row.InverseAction.Should().Be("exchange.address.remove");
        row.TargetId.Should().Be(UserId);
    }

    [Fact]
    public async Task AddAddress_AlreadyThere_IsANoOp_AndSendsNoWrite()
    {
        _handler.Enqueue(HttpStatusCode.OK, Mailbox(r => r["EmailAddresses"] = Addresses("SMTP:Adele@contoso.com", "smtp:sales@contoso.com")));

        var outcome = await Sut.AddAddressAsync(Target, "SALES@contoso.com", CancellationToken.None);

        outcome.WasNoOp.Should().BeTrue();
        _handler.Requests.Should().ContainSingle("only the read");
    }

    [Fact]
    public async Task AddAddress_Taken_SaysWhoHasIt()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, Mailbox())
            .Enqueue(HttpStatusCode.Conflict, Fixtures.Text("exchange/error-address-taken"));

        var outcome = await Sut.AddAddressAsync(Target, "info@contoso.com", CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Be("That address is already used by Contoso Info. An address can belong to only one recipient.");
        outcome.Details.Should().Contain("ProxyAddressExistsException");
        _actions.Get(outcome.ActionId)!.Outcome.Should().Be(ActionOutcome.Failed);
    }

    [Fact]
    public void ForeignDomain_And_RemovingThePrimary_HaveTheirOwnSentences()
    {
        var domain = ExchangeException.FromResponse(400, Fixtures.Text("exchange/error-domain-not-accepted"), null, "Set-Mailbox", DateTimeOffset.UtcNow);
        var primary = ExchangeException.FromResponse(400, Fixtures.Text("exchange/error-no-primary-address"), null, "Set-Mailbox", DateTimeOffset.UtcNow);
        var hold = ExchangeException.FromResponse(400, Fixtures.Text("exchange/error-hold-needs-licence"), null, "Set-Mailbox", DateTimeOffset.UtcNow);

        ExchangeErrorText.Friendly(domain).Should().StartWith("That domain isn't one this tenant accepts mail for");
        ExchangeErrorText.Friendly(primary).Should().StartWith("That's the primary address");
        ExchangeErrorText.Friendly(hold).Should().Contain("Exchange Online Plan 2");
    }

    [Fact]
    public async Task RemovingThePrimary_IsRefusedBeforeExchangeIsAsked()
    {
        _handler.Enqueue(HttpStatusCode.OK, Mailbox());

        var outcome = await Sut.RemoveAddressAsync(Target, "Adele@contoso.com", CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().StartWith("That's the primary address");
        _handler.Requests.Should().ContainSingle("the refusal comes from the read, not from a failed write");
    }

    [Fact]
    public async Task SetPrimary_UsesWindowsEmailAddress_AndUndoGoesBack()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["EmailAddresses"] = Addresses("SMTP:Adele@contoso.com", "smtp:sales@contoso.com")))
            .Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["PrimarySmtpAddress"] = "sales@contoso.com"))
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["PrimarySmtpAddress"] = "sales@contoso.com"));

        var outcome = await Sut.SetPrimaryAddressAsync(Target, "sales@contoso.com", CancellationToken.None);

        outcome.Success.Should().BeTrue();
        _handler.Requests[1].Body.Should().Contain("\"WindowsEmailAddress\":\"sales@contoso.com\"").And.NotContain("PrimarySmtpAddress",
            "-PrimarySmtpAddress answers 403 through this endpoint");

        _handler
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["PrimarySmtpAddress"] = "sales@contoso.com"))
            .Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, Mailbox())
            .Enqueue(HttpStatusCode.OK, Mailbox());
        var undone = await outcome.Undo!(CancellationToken.None);

        undone.Success.Should().BeTrue();
        _handler.Requests[5].Body.Should().Contain("\"WindowsEmailAddress\":\"Adele@contoso.com\"");
        _actions.Get(outcome.ActionId)!.UndoneAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AWriteExchangeDoesNotShowYet_SucceedsButSaysSo()
    {
        _handler.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK);
        for (var i = 0; i < 7; i++) _handler.Enqueue(HttpStatusCode.OK, Mailbox());   // never converges

        var outcome = await Sut.SetHiddenAsync(Target, true, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Summary.Should().Contain("isn't showing it yet");
        _handler.Requests.Should().HaveCount(9, "one read, one write, seven read-backs");
    }

    [Fact]
    public async Task OneAgreeingReadIsNotConfirmation_TwoInARowAre()
    {
        // The M14 gate: an undo read back correctly once, then the earlier write overturned it (doc 13 §5).
        _handler.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true))   // agrees once
            .Enqueue(HttpStatusCode.OK, Mailbox())                                                 // then flips back
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true))
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true));  // two in a row

        var outcome = await Sut.SetHiddenAsync(Target, true, CancellationToken.None);

        outcome.Summary.Should().NotContain("isn't showing it yet");
        _handler.Requests.Should().HaveCount(6);
    }

    [Fact]
    public async Task TheNextWriteToTheSameMailbox_WaitsForItToSettle_AndAnotherMailboxDoesNot()
    {
        var time = new FakeTimeProvider();
        var waits = new List<TimeSpan>();
        var sut = MailboxSummaryTests.Service(_handler, actions: _actions, time: time, delays: waits);
        _handler.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true))
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true));
        await sut.SetHiddenAsync(Target, true, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(4));
        sut.TimeUntilSettled(UserId).Should().Be(TimeSpan.FromSeconds(11));
        sut.TimeUntilSettled("someone-else").Should().Be(TimeSpan.Zero);

        waits.Clear();
        _handler.Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true)).Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK, Mailbox());
        await sut.SetHiddenAsync(Target, false, CancellationToken.None);

        waits[0].Should().Be(TimeSpan.FromSeconds(11), "the second write waits out the rest of the 15-second gap before it even reads");
    }

    [Fact]
    public async Task AWriteToADifferentSettingOfTheSameMailbox_DoesNotWait()
    {
        // M15: an offboarding writes eleven settings to one mailbox in a row. The out-of-order landing doc 13 §5 measured
        // was one attribute flipping back, so the gap is per setting; a gap per mailbox would add minutes to every run.
        var time = new FakeTimeProvider();
        var waits = new List<TimeSpan>();
        var sut = MailboxSummaryTests.Service(_handler, actions: _actions, time: time, delays: waits);
        _handler.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true))
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["HiddenFromAddressListsEnabled"] = true));
        await sut.SetHiddenAsync(Target, true, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));

        sut.TimeUntilSettled(UserId, "exchange.hidden").Should().Be(TimeSpan.FromSeconds(11));
        sut.TimeUntilSettled(UserId, "exchange.hold").Should().Be(TimeSpan.Zero);

        waits.Clear();
        _handler.Enqueue(HttpStatusCode.OK, Mailbox()).Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["LitigationHoldEnabled"] = true))
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["LitigationHoldEnabled"] = true));
        (await sut.SetLitigationHoldAsync(Target, true, null, CancellationToken.None)).Success.Should().BeTrue();

        waits.Should().Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1.5)], "only the two confirm reads' delays, and no settle wait before the write");
    }

    [Fact]
    public async Task SendOnBehalf_ChangesTheListByName_NotByReplacingIt()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, """{"value":[{"Name":"MeganBowen"}]}""")      // Get-Recipient: the name the list will hold
            .Enqueue(HttpStatusCode.OK, Mailbox())
            .Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["GrantSendOnBehalfTo"] = Addresses("MeganBowen")))
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["GrantSendOnBehalfTo"] = Addresses("MeganBowen")));

        var outcome = await Sut.ChangeListAsync(Target, MailboxList.SendOnBehalf, "obj-megan", "Megan Bowen", add: true, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Summary.Should().Be("Added Megan Bowen to Send on Behalf on Adele Vance");
        _handler.Requests[2].Body.Should().Contain("\"GrantSendOnBehalfTo\":{\"@odata.type\":\"#Exchange.GenericHashTable\",\"add\":[\"obj-megan\"]}");
    }

    [Fact]
    public async Task FullAccess_GrantsByUpn_WithTheAutomappingChoice_AndConfirmsFromThePermissionList()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mailbox-permissions-self"))
            .Enqueue(HttpStatusCode.OK, """{"value":[{"User":"megan@contoso.com"}]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"User":"megan@contoso.com","AccessRights":["FullAccess"],"IsInherited":false,"Deny":false}]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"User":"megan@contoso.com","AccessRights":["FullAccess"],"IsInherited":false,"Deny":false}]}""");

        var outcome = await Sut.GrantFullAccessAsync(Target, "megan@contoso.com", autoMapping: false, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Summary.Should().Be("Gave megan@contoso.com Full Access to Adele Vance");
        _handler.Requests[1].Body.Should().Contain("\"Add-MailboxPermission\"").And.Contain("\"AutoMapping\":false").And.Contain("\"AccessRights\":[\"FullAccess\"]");
    }

    [Fact]
    public async Task AutoReply_MessagesNeverReachTheActionLog()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/auto-reply-configuration"))
            .Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, """{"value":[{"AutoReplyState":"Enabled"}]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"AutoReplyState":"Enabled"}]}""");

        var outcome = await Sut.SetAutoReplyAsync(Target,
            new AutoReplySettings { State = "Enabled", InternalMessage = "Private: off sick until Friday", ExternalAudience = "None" }, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        _handler.Requests[1].Body.Should().Contain("off sick", "Exchange has to receive the message");
        var row = _actions.Get(outcome.ActionId)!;
        (row.RequestSummary + row.Message + row.InversePayload).Should().NotContain("off sick");
    }

    [Fact]
    public async Task FolderPermission_NewPersonIsAdded_ExistingIsSet_AndDefaultCannotBeRemoved()
    {
        var calendar = Fixtures.Text("exchange/folder-statistics-calendar");
        var perms = Fixtures.Text("exchange/folder-permissions-calendar");

        _handler
            .Enqueue(HttpStatusCode.OK, calendar).Enqueue(HttpStatusCode.OK, perms)   // read
            .Enqueue(HttpStatusCode.OK)                                                // Add-
            .Enqueue(HttpStatusCode.OK, calendar)
            .Enqueue(HttpStatusCode.OK, """{"value":[{"User":"Megan Bowen","AccessRights":["Reviewer"]}]}""")
            .Enqueue(HttpStatusCode.OK, calendar)
            .Enqueue(HttpStatusCode.OK, """{"value":[{"User":"Megan Bowen","AccessRights":["Reviewer"]}]}""");
        var add = await Sut.SetFolderPermissionAsync(Target, "Calendar", "obj-megan", "Megan Bowen", "Reviewer", CancellationToken.None);
        add.Success.Should().BeTrue();
        _handler.Requests[2].Body.Should().Contain("\"Add-MailboxFolderPermission\"").And.Contain($"{UserId}:\\\\Calendar");

        _handler.Enqueue(HttpStatusCode.OK, calendar).Enqueue(HttpStatusCode.OK, perms)
            .Enqueue(HttpStatusCode.OK)
            .Enqueue(HttpStatusCode.OK, calendar)
            .Enqueue(HttpStatusCode.OK, """{"value":[{"User":"Default","AccessRights":["LimitedDetails"]}]}""")
            .Enqueue(HttpStatusCode.OK, calendar)
            .Enqueue(HttpStatusCode.OK, """{"value":[{"User":"Default","AccessRights":["LimitedDetails"]}]}""");
        var set = await Sut.SetFolderPermissionAsync(Target, "Calendar", "Default", "Default", "LimitedDetails", CancellationToken.None);
        set.Success.Should().BeTrue();
        _handler.Requests[9].Body.Should().Contain("\"Set-MailboxFolderPermission\"");

        _handler.Enqueue(HttpStatusCode.OK, calendar).Enqueue(HttpStatusCode.OK, perms);
        var remove = await Sut.SetFolderPermissionAsync(Target, "Calendar", "Default", "Default", null, CancellationToken.None);
        remove.Success.Should().BeFalse();
        remove.FriendlyError.Should().Contain("set them to None");
    }

    [Fact]
    public async Task Archive_IsNotUndoable_AndIsANoOpWhenAlreadyOn()
    {
        _handler
            .Enqueue(HttpStatusCode.OK, Mailbox())
            .Enqueue(HttpStatusCode.OK, """{"value":[{}]}""")
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["ArchiveStatus"] = "Active"))
            .Enqueue(HttpStatusCode.OK, Mailbox(r => r["ArchiveStatus"] = "Active"));

        var outcome = await Sut.EnableArchiveAsync(Target, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.CanUndo.Should().BeFalse("disabling an archive starts a 30-day countdown to deleting it");
        _actions.Get(outcome.ActionId)!.InverseAction.Should().BeNull();

        _handler.Enqueue(HttpStatusCode.OK, Mailbox(r => r["ArchiveStatus"] = "Active"));
        (await Sut.EnableArchiveAsync(Target, CancellationToken.None)).WasNoOp.Should().BeTrue();
    }

    [Fact]
    public async Task Convert_RefusesWhatIsNeitherUserNorShared()
    {
        _handler.Enqueue(HttpStatusCode.OK, Mailbox(r => r["RecipientTypeDetails"] = "RoomMailbox"));

        var outcome = await Sut.ConvertAsync(Target, toShared: true, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("room mailbox");
    }

    [Fact]
    public async Task AddressCheck_HasThreeAnswers()
    {
        _handler
            .Enqueue(HttpStatusCode.NotFound, Fixtures.Text("exchange/error-not-found"))
            .Enqueue(HttpStatusCode.OK, """{"value":[{"DisplayName":"Contoso Info","PrimarySmtpAddress":"info@contoso.com"}]}""")
            .Enqueue(HttpStatusCode.BadRequest, Fixtures.Text("exchange/error-bad-parameter"));

        (await Sut.CheckAddressAsync("new@contoso.com", CancellationToken.None)).Availability.Should().Be(AddressAvailability.Free);
        var taken = await Sut.CheckAddressAsync("info@contoso.com", CancellationToken.None);
        taken.Availability.Should().Be(AddressAvailability.Taken);
        taken.Owner.Should().Be("Contoso Info");
        (await Sut.CheckAddressAsync("x@contoso.com", CancellationToken.None)).Availability.Should().Be(AddressAvailability.CouldNotCheck,
            "a check that failed must never read as free");
    }

    [Fact]
    public async Task ResolveRecipients_TurnsExchangeNamesIntoPeople_AndKeepsWhatDoesNotResolve()
    {
        _handler.Enqueue(HttpStatusCode.OK,
            """{"value":[{"Name":"MeganBowen","DisplayName":"Megan Bowen","PrimarySmtpAddress":"megan@contoso.com","ExternalDirectoryObjectId":"obj-megan"}]}""");

        var map = await Sut.ResolveRecipientsAsync(["MeganBowen", "Gone.Person"], CancellationToken.None);

        map["MeganBowen"].Display.Should().Be("Megan Bowen (megan@contoso.com)");
        map["MeganBowen"].Identity.Should().Be("obj-megan");
        map["Gone.Person"].Display.Should().Be("Gone.Person");
        map["Gone.Person"].Identity.Should().Be("Gone.Person");
        _handler.Requests.Single().Body.Should().Contain("Name -eq 'MeganBowen' -or Name -eq 'Gone.Person'");
    }
}
