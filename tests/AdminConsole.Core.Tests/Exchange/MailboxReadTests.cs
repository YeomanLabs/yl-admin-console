using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Exchange;

/// <summary>M13.5 — the Mailbox tab's reads, parsed from responses captured from a real tenant.</summary>
public sealed class MailboxReadTests
{
    private const string UserId = "00000000-0000-0000-0000-000000000101";

    [Fact]
    public void Mailbox_ParsesEverySection_FromARealGetMailbox()
    {
        var m = MailboxDetail.FromJson(UserId, Fixtures.Item("exchange/mailbox", 0));

        m.Kind.Should().Be(MailboxKind.User);
        m.PrimarySmtp.Should().Be("Adele@contoso.com");
        m.EmailAddresses[0].Should().Be(new ProxyAddress("SMTP", "Adele@contoso.com"), "the primary sorts first");
        m.EmailAddresses[0].IsPrimary.Should().BeTrue();
        m.EmailAddresses.Should().Contain(a => a.Prefix == "smtp" && !a.IsPrimary && a.IsSmtp);
        m.EmailAddresses.Should().Contain(a => a.Prefix == "SIP" && a.Kind == "Teams / SIP");
        m.HiddenFromAddressLists.Should().BeFalse();
        m.IsForwarding.Should().BeFalse();
        m.SendOnBehalf.Should().BeEmpty();
        m.HasArchive.Should().BeFalse("ArchiveStatus is None and the archive GUID is all zeros");
        m.LitigationHold.Should().BeFalse();
        m.LitigationHoldDuration.Should().Be("Unlimited");
        m.LitigationHoldOwner.Should().BeNull("Exchange reports an empty string, which is no owner");
        m.ProhibitSendReceiveQuota.Bytes.Should().Be(107_374_182_400L);
        m.Created.Should().Be(new DateTimeOffset(2026, 7, 14, 11, 20, 36, TimeSpan.Zero));
        m.HasDeliveryRestrictions.Should().BeFalse();
    }

    [Fact]
    public void Mailbox_SelectNamesOnlyPropertiesARealMailboxCarries()
    {
        // $select silently skips unknown names (doc 13 §1), so a typo here would show up as a blank field, not an error.
        using var doc = JsonDocument.Parse(Fixtures.Text("exchange/mailbox"));
        var row = doc.RootElement.GetProperty("value")[0];
        var missing = MailboxDetail.Select.Where(p => !row.TryGetProperty(p, out var unused)).ToList();

        missing.Should().BeEmpty("every selected property exists on a real mailbox");
    }

    [Fact]
    public void Usage_ReadsTheByteCountsAndTheTimes()
    {
        var u = MailboxUsage.FromJson(Fixtures.Item("exchange/mailbox-statistics", 0));

        u.TotalItemSize.Bytes.Should().Be(86_646_374L);
        u.DeletedItemSize.Bytes.Should().Be(10_128_522L);
        u.ItemCount.Should().Be(920);
        u.LastUserAction.Should().NotBeNull();
    }

    [Fact]
    public void FullAccess_DropsSelf_AndKeepsWhatSomeoneGranted()
    {
        var entries = MailboxPermissionEntry.FromFullAccessRows(Fixtures.Items("exchange/mailbox-permissions-shared"));

        entries.Select(e => e.Principal).Should().Equal("Adele@contoso.com", "Megan@contoso.com");
        entries.Should().OnlyContain(e => e.Rights.SequenceEqual(new[] { "FullAccess" }) && !e.IsDeny);
    }

    [Fact]
    public void FullAccess_OnAMailboxNobodyShares_IsEmpty()
    {
        MailboxPermissionEntry.FromFullAccessRows(Fixtures.Items("exchange/mailbox-permissions-self")).Should().BeEmpty();
    }

    [Fact]
    public void SendAs_DropsSelf()
    {
        var entries = MailboxPermissionEntry.FromSendAsRows(Fixtures.Items("exchange/recipient-permissions-shared"));

        entries.Select(e => e.Principal).Should().Equal("Adele@contoso.com", "Megan@contoso.com");
        entries.Should().OnlyContain(e => e.Rights.Contains("SendAs"));
    }

    [Fact]
    public void Regional_And_AutoReply()
    {
        var r = RegionalSettings.FromJson(Fixtures.Item("exchange/regional-configuration", 0));
        r.Language.Should().Be("en-US");
        r.TimeZone.Should().Be("Central Standard Time");
        r.IsSet.Should().BeTrue();

        var a = AutoReplySettings.FromJson(Fixtures.Item("exchange/auto-reply-configuration", 0));
        a.IsOn.Should().BeFalse();
        a.ExternalAudience.Should().Be("All");
        a.InternalMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("<html><body><p>I'm away until <b>Monday</b>.</p><p>Ask Megan&nbsp;Bowen.</p></body></html>", "I'm away until Monday. Ask Megan Bowen.")]
    [InlineData("Plain text", "Plain text")]
    [InlineData("<div></div>", null)]
    [InlineData(null, null)]
    public void AutoReply_PreviewIsPlainText(string? html, string? text)
    {
        AutoReplySettings.PlainText(html).Should().Be(text);
    }

    [Fact]
    public void Folder_IsFoundByType_NotByItsEnglishName()
    {
        var rows = Fixtures.Items("exchange/folder-statistics-calendar");

        var folder = MailboxFolder.FindDefault(rows, "Calendar")!;
        folder.Name.Should().Be("Calendar");
        folder.IdentityFor(UserId).Should().Be(UserId + ":\\Calendar");

        // A German mailbox: same type, localised name and path.
        var german = MailboxFolder.FindDefault([Fixtures.Parse("""{"Name":"Kalender","FolderPath":"/Kalender","FolderType":"Calendar"}""")], "Calendar")!;
        german.IdentityFor(UserId).Should().Be(UserId + ":\\Kalender");

        MailboxFolder.FindDefault(rows, "Inbox").Should().BeNull("the Calendar scope does not contain the Inbox");
    }

    [Fact]
    public void FolderPermissions_NameDefaultAndAnonymousInWords()
    {
        var entries = Fixtures.Items("exchange/folder-permissions-calendar").Select(FolderPermissionEntry.FromJson).ToList();

        entries.Should().HaveCount(2);
        entries[0].IsDefault.Should().BeTrue();
        entries[0].Rights.Should().Equal("AvailabilityOnly");
        entries[0].Who.Should().StartWith("Everyone in the organisation");
        entries[1].IsAnonymous.Should().BeTrue();
    }

    [Fact]
    public async Task Service_ReadsFolderPermissions_ByResolvingTheFolderFirst()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/folder-statistics-calendar"))
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/folder-permissions-calendar"));
        var sut = MailboxSummaryTests.Service(handler);

        var section = await sut.GetFolderPermissionsAsync(UserId, "Calendar", CancellationToken.None);

        section.IsSuccess.Should().BeTrue();
        section.Value!.Entries.Should().HaveCount(2);
        handler.Requests[0].Body.Should().Contain("\"FolderScope\":\"Calendar\"");
        handler.Requests[1].Body.Should().Contain($"\"Identity\":\"{UserId}:\\\\Calendar\"");
    }

    [Fact]
    public async Task Service_ASectionThatFails_CarriesItsOwnSentence_AndDoesNotThrow()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.NotFound, Fixtures.Text("exchange/error-not-found"));
        var sut = MailboxSummaryTests.Service(handler);

        var section = await sut.GetMailboxAsync(UserId, CancellationToken.None);

        section.IsLoaded.Should().BeTrue();
        section.IsNotFound.Should().BeTrue();
        section.Message.Should().StartWith("Exchange can't find that mailbox");
    }

    [Fact]
    public async Task Service_RoutesEveryPerMailboxRead_ByTheTargetMailbox_NotTheAdmins()
    {
        // The M13 gate's finding: routed by the admin's mailbox, Get-MailboxRegionalConfiguration on anyone else's
        // answers "Cmdlet needs proxy". Every read about one mailbox is routed by that mailbox.
        var handler = new FakeHttpHandler();
        for (var i = 0; i < 8; i++) handler.Enqueue(HttpStatusCode.OK, """{"value":[{"Name":"Calendar","FolderPath":"/Calendar","FolderType":"Calendar"}]}""");
        var sut = MailboxSummaryTests.Service(handler);

        await sut.GetMailboxAsync(UserId, CancellationToken.None);
        await sut.GetUsageAsync(UserId, archive: false, CancellationToken.None);
        await sut.GetFullAccessAsync(UserId, CancellationToken.None);
        await sut.GetSendAsAsync(UserId, CancellationToken.None);
        await sut.GetRegionalAsync(UserId, CancellationToken.None);
        await sut.GetAutoReplyAsync(UserId, CancellationToken.None);
        await sut.GetFolderPermissionsAsync(UserId, "Calendar", CancellationToken.None);

        handler.Requests.Should().HaveCount(8);
        handler.Requests.Should().OnlyContain(r => r.Headers["X-AnchorMailbox"] == $"OID:{UserId}@t-1");
    }

    [Fact]
    public async Task Service_TenantWideWalks_StayRoutedByTheAdmin()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """{"value":[]}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await MailboxSummaryTests.Service(handler).GetMailboxSummariesAsync(CancellationToken.None);

        handler.Requests.Should().OnlyContain(r => r.Headers["X-AnchorMailbox"] == "UPN:admin@contoso.com");
    }

    [Fact]
    public async Task Service_ArchiveUsage_AsksForTheArchive()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mailbox-statistics"));
        var sut = MailboxSummaryTests.Service(handler);

        await sut.GetUsageAsync(UserId, archive: true, CancellationToken.None);

        handler.Requests.Single().Body.Should().Contain("\"Archive\":true");
    }
}
