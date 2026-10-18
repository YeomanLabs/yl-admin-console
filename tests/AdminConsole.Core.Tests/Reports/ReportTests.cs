using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Reports;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Reports;

/// <summary>M19: the three reports and where they are kept.</summary>
public sealed class ReportTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly FakeHttpHandler _http = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly GraphClient _graph;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ac-reports-" + Guid.NewGuid().ToString("N"));

    public ReportTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        _graph = new GraphClient(new HttpClient(_http), new FakeAuthService(), NullLogger<GraphClient>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---- MFA ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "microsoftAuthenticatorPush", "mobilePhone" }, true, false, true, null, MfaStatus.Ready)]
    [InlineData(new[] { "mobilePhone" }, true, false, true, null, MfaStatus.PhoneOnly)]
    [InlineData(new[] { "email" }, false, false, true, null, MfaStatus.Missing)]
    [InlineData(new string[0], false, false, false, null, MfaStatus.Exempt)]
    [InlineData(new string[0], false, true, true, null, MfaStatus.Exempt)]
    [InlineData(new string[0], false, false, true, MailboxKind.Shared, MfaStatus.Exempt)]
    public void Classify_DecidesExemptionFirst_ThenStrength(string[] methods, bool registered, bool guest, bool enabled, MailboxKind? mailbox, MfaStatus expected)
    {
        MfaReportService.Classify(methods, registered, guest, enabled, mailbox).Item1.Should().Be(expected);
    }

    [Fact]
    public void SyncAccount_IsRecognised_ByItsUpnOrItsName()
    {
        MfaReportService.IsSyncAccount("Sync_DC01_1a2b3c4d5e6f@contoso.onmicrosoft.com", null).Should().BeTrue();
        MfaReportService.IsSyncAccount("x@contoso.com", "On-Premises Directory Synchronization Service Account").Should().BeTrue();
        MfaReportService.IsSyncAccount("sync.team@contoso.com", "Sync Team").Should().BeFalse();
    }

    [Fact]
    public async Task Mfa_PagesThroughEveryone_JoinsTheCache_AndCountsWhatThePortalCounts()
    {
        _db.Write(w =>
        {
            w.UpsertUser(User("u1", "Ada", enabled: true));
            w.UpsertUser(User("u2", "Bea", enabled: false));
            w.UpsertUser(User("u3", "Shared Box", enabled: true));
            w.UpsertMailbox(new MailboxSummary { UserId = "u3", RecipientType = "SharedMailbox" });
        });
        _http.Enqueue(HttpStatusCode.OK, """
            {"value":[{"id":"u1","userPrincipalName":"ada@contoso.com","userDisplayName":"Ada","userType":"member","isAdmin":true,
                       "isMfaRegistered":false,"isMfaCapable":false,"methodsRegistered":[],"lastUpdatedDateTime":"2026-10-17T08:00:00Z"}],
             "@odata.nextLink":"https://graph.microsoft.com/v1.0/reports/authenticationMethods/userRegistrationDetails?$skiptoken=x"}
            """)
            .Enqueue(HttpStatusCode.OK, """
            {"value":[{"id":"u2","userPrincipalName":"bea@contoso.com","userDisplayName":"Bea","userType":"member","methodsRegistered":[]},
                      {"id":"u3","userPrincipalName":"shared@contoso.com","userDisplayName":"Shared Box","userType":"member","methodsRegistered":[]},
                      {"id":"u4","userPrincipalName":"cy@contoso.com","userDisplayName":"Cy","userType":"member","isMfaRegistered":true,"isMfaCapable":true,
                       "methodsRegistered":["microsoftAuthenticatorPush"]}]}
            """);

        var report = await new MfaReportService(_graph, _db, NullLogger<MfaReportService>.Instance).BuildAsync(Ct);

        report.Rows.Should().HaveCount(4, "both pages");
        report.Rows.Single(r => r.UserId == "u1").Status.Should().Be(MfaStatus.Missing);
        report.AdminsNotReady.Should().Be(1);
        report.Rows.Single(r => r.UserId == "u2").Detail.Should().Be("Sign-in is blocked");
        report.Rows.Single(r => r.UserId == "u3").Status.Should().Be(MfaStatus.Exempt, "a shared mailbox's account is not a person");
        report.Rows.Single(r => r.UserId == "u4").Status.Should().Be(MfaStatus.Ready);
        report.MfaCapable.Should().Be(1);
        report.Rows[0].UserId.Should().Be("u4", "rows sort by status, ready first");
        report.SourceUpdatedAt.Should().Be(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        report.BlockedLeftOut.Should().Be(0, "Bea is blocked but listed, so she is not counted as left out");
    }

    // ---- licences -------------------------------------------------------------------------------------------------

    [Fact]
    public void Held_TellsDirectFromGroup_AndSkipsAFailedGroupAssignment()
    {
        using var doc = JsonDocument.Parse("""
            {"licenseAssignmentStates":[{"skuId":"e3","assignedByGroup":null,"state":"Active"},{"skuId":"e3","assignedByGroup":"g1","state":"Active"},
                                         {"skuId":"visio","assignedByGroup":"g2","state":"Active"},{"skuId":"p1","assignedByGroup":"g3","state":"Error"}]}
            """);

        var held = LicenceReportService.Held(doc.RootElement);

        held.Should().BeEquivalentTo(new[] { ("e3", (true, true)), ("visio", (false, true)) });
    }

    [Fact]
    public async Task Licences_SeatsFromSubscribedSkus_FlagsBlockedAndQuiet()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-200).ToString("o");
        var recent = DateTimeOffset.UtcNow.AddDays(-3).ToString("o");
        _http.Enqueue(HttpStatusCode.OK, """
            {"value":[{"skuId":"e3","skuPartNumber":"SPE_E3","prepaidUnits":{"enabled":3,"warning":0,"suspended":0},"consumedUnits":3},
                      {"skuId":"flow","skuPartNumber":"FLOW_FREE","prepaidUnits":{"enabled":10000},"consumedUnits":2}]}
            """)
            .Enqueue(HttpStatusCode.OK, $$$"""
            {"value":[
              {"id":"u1","displayName":"Active","userPrincipalName":"a@c.com","accountEnabled":true,"createdDateTime":"{{{old}}}",
               "licenseAssignmentStates":[{"skuId":"e3","assignedByGroup":null,"state":"Active"}],"signInActivity":{"lastSignInDateTime":"{{{recent}}}"}},
              {"id":"u2","displayName":"Blocked","userPrincipalName":"b@c.com","accountEnabled":false,"createdDateTime":"{{{old}}}",
               "licenseAssignmentStates":[{"skuId":"e3","assignedByGroup":"g1","state":"Active"}]},
              {"id":"u3","displayName":"Quiet","userPrincipalName":"q@c.com","accountEnabled":true,"createdDateTime":"{{{old}}}",
               "licenseAssignmentStates":[{"skuId":"e3","assignedByGroup":null,"state":"Active"}],"signInActivity":{"lastSignInDateTime":"{{{old}}}"}},
              {"id":"u4","displayName":"Unlicensed","userPrincipalName":"n@c.com","accountEnabled":true,"licenseAssignmentStates":[]}]}
            """);

        var report = await new LicenceReportService(_graph, NullLogger<LicenceReportService>.Instance).BuildAsync(Ct);

        var e3 = report.Skus[0];
        e3.PartNumber.Should().Be("SPE_E3", "paid subscriptions sort before free ones");
        (e3.Enabled, e3.Consumed, e3.Available, e3.Direct, e3.ViaGroup, e3.Disabled, e3.Quiet).Should().Be((3, 3, 0, 2, 1, 1, 1));
        report.Skus[1].LooksFree.Should().BeTrue();
        report.Skus.Should().NotContain(s => s.IsSuspended);
        report.Flagged.Select(f => f.DisplayName).Should().Equal(["Blocked", "Quiet"], "blocked first, then the quietest");
        report.Flagged[1].Reason.Should().StartWith("No sign-in since");

        // 0.3.3: a subscription lists everyone who holds it, and how.
        report.Holders.Select(h => h.DisplayName).Should().Equal(["Active", "Blocked", "Quiet"], "everyone licensed, by name; the unlicensed account is not a holder");
        report.HoldersOf("e3").Should().HaveCount(3);
        report.HoldersOf("flow").Should().BeEmpty();
        report.Holders.Single(h => h.DisplayName == "Blocked").HowHeld("e3").Should().Be("Group");
        report.Holders.Single(h => h.DisplayName == "Active").Reason.Should().StartWith("Last signed in");
        report.QuietUnavailable.Should().BeNull();
        _http.Requests[1].Uri.ToString().Should().Contain("signInActivity");
    }

    [Fact]
    public async Task Licences_WithoutSignInActivity_StillCountSeats_AndSayQuietCannotBeJudged()
    {
        _http.Enqueue(HttpStatusCode.OK, """{"value":[{"skuId":"e3","skuPartNumber":"SPE_E3","prepaidUnits":{"enabled":1},"consumedUnits":1}]}""")
            .Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authentication_RequestFromNonPremiumTenantOrB2CTenant","message":"Neither tenant is B2C or tenant doesn't have premium license"}}""")
            .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"u1","displayName":"A","userPrincipalName":"a@c.com","accountEnabled":true,"licenseAssignmentStates":[{"skuId":"e3","state":"Active"}]}]}""");

        var report = await new LicenceReportService(_graph, NullLogger<LicenceReportService>.Instance).BuildAsync(Ct);

        report.QuietUnavailable.Should().Contain("P1");
        report.Skus.Single().Direct.Should().Be(1);
        report.Flagged.Should().BeEmpty("nobody is called quiet on no evidence");
        _http.Requests[2].Uri.ToString().Should().NotContain("signInActivity");
    }

    // ---- mailboxes ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mailboxes_TwoTenantWideCalls_ThenSizeAndFullAccessPerMailbox()
    {
        var exchange = new ExchangeService(new ExchangeClient(new HttpClient(_http), new FakeAuthService(), _tenant, NullLogger<ExchangeClient>.Instance),
            _tenant, _actions, NullLogger<ExchangeService>.Instance);
        _http.Responder = req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            var cmdlet = JsonDocument.Parse(body).RootElement.GetProperty("CmdletInput").GetProperty("CmdletName").GetString();
            var anchor = req.Headers.TryGetValues("X-AnchorMailbox", out var a) ? a.First() : string.Empty;
            return cmdlet switch
            {
                "Get-Mailbox" => FakeHttpHandler.Json(HttpStatusCode.OK, """
                    {"value":[{"ExternalDirectoryObjectId":"m1","Identity":"ada","Name":"ada","DisplayName":"Ada","PrimarySmtpAddress":"ada@c.com","RecipientTypeDetails":"UserMailbox","GrantSendOnBehalfTo":[]},
                              {"ExternalDirectoryObjectId":"m2","Identity":"info","Name":"info","DisplayName":"Info","PrimarySmtpAddress":"info@c.com","RecipientTypeDetails":"SharedMailbox","GrantSendOnBehalfTo":["Ada"]}]}
                    """),
                "Get-RecipientPermission" => FakeHttpHandler.Json(HttpStatusCode.OK, """
                    {"value":[{"Identity":"info","Trustee":"ada@c.com","AccessRights":["SendAs"],"IsInherited":false,"AccessControlType":"Allow"},
                              {"Identity":"ada","Trustee":"NT AUTHORITY\\SELF","AccessRights":["SendAs"],"IsInherited":false,"AccessControlType":"Allow"}]}
                    """),
                "Get-MailboxStatistics" => FakeHttpHandler.Json(HttpStatusCode.OK, anchor.Contains("m1")
                    ? """{"value":[{"TotalItemSize":"1.5 MB (1,572,864 bytes)","ItemCount":10}]}"""
                    : """{"value":[{"TotalItemSize":"3 GB (3,221,225,472 bytes)","ItemCount":900}]}"""),
                "Get-MailboxPermission" => FakeHttpHandler.Json(HttpStatusCode.OK, anchor.Contains("m2")
                    ? """{"value":[{"User":"ada@c.com","AccessRights":["FullAccess"],"IsInherited":false,"Deny":false}]}"""
                    : """{"value":[{"User":"NT AUTHORITY\\SELF","AccessRights":["FullAccess"],"IsInherited":false,"Deny":false}]}"""),
                _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"value":[]}"""),
            };
        };

        var progress = new List<(int, int)>();
        var report = await new MailboxReportService(exchange, _db, NullLogger<MailboxReportService>.Instance)
            .BuildAsync(new Progress<(int Done, int Total)>(p => { lock (progress) progress.Add(p); }), Ct);

        report.Rows.Select(r => r.Name).Should().Equal(["Info", "Ada"], "largest first");
        var info = report.Rows[0];
        info.SizeBytes.Should().Be(3_221_225_472L);
        info.FullAccess.Should().Equal(["ada@c.com"]);
        info.SendAs.Should().Equal(["ada@c.com"]);
        info.SendOnBehalf.Should().Equal(["Ada"]);
        report.Rows[1].IsDelegated.Should().BeFalse("SELF is nobody else");
        report.Delegated.Should().Be(1);
        report.TotalBytes.Should().Be(3_221_225_472L + 1_572_864L);
        _http.Requests.Count(r => r.Body!.Contains("Get-RecipientPermission")).Should().Be(1, "Send As is one tenant-wide call");
    }

    [Fact]
    public void ReportRows_AreEqualOnlyToThemselves_SoARebuiltListStaysReachableToAScreenReader()
    {
        // WPF keys a list's automation peers by item equality; a rebuilt row equal by value to the old one took over
        // the old row's detached peer, and the row dropped out of UI Automation (found in the 0.3.3 screenshot run).
        var a = new LicenceSkuRow { SkuId = "e3", PartNumber = "SPE_E3", Name = "Microsoft 365 E3", Enabled = 3, Consumed = 3 };
        var b = a with { };

        a.Equals(b).Should().BeFalse();
        a.Equals(a).Should().BeTrue();
        new MfaRow { UserId = "u", DisplayName = "U", Upn = "u@c", Status = MfaStatus.Ready, Detail = "d" }
            .Equals(new MfaRow { UserId = "u", DisplayName = "U", Upn = "u@c", Status = MfaStatus.Ready, Detail = "d" }).Should().BeFalse();
        a.ToString().Should().Be("Microsoft 365 E3, 3 of 3 seats assigned", "what a screen reader reads for the row, not a record dump");
    }

    // ---- store ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Store_KeepsAReportPerTenantWithItsDate_AndShrugsOffADamagedFile()
    {
        var store = new ReportStore(_dir);
        var at = new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero);
        store.Save("t-1", "mfa", new MfaReport { TakenAt = at, Rows = [new MfaRow { UserId = "u", DisplayName = "U", Upn = "u@c.com", Status = MfaStatus.PhoneOnly, Detail = "Phone only" }] });

        var back = store.Load<MfaReport>("t-1", "mfa")!;
        back.TakenAt.Should().Be(at);
        back.Rows.Single().Status.Should().Be(MfaStatus.PhoneOnly);
        store.Load<MfaReport>("t-2", "mfa").Should().BeNull("another tenant has its own");

        File.WriteAllText(Path.Combine(_dir, "t-1", "mfa.json"), "{ not json");
        store.Load<MfaReport>("t-1", "mfa").Should().BeNull();
    }

    private static DirectoryUser User(string id, string name, bool enabled) => DirectoryUser.FromJson(Fixtures.Parse($$"""
        {"id":"{{id}}","displayName":"{{name}}","userPrincipalName":"{{id}}@contoso.com","accountEnabled":{{(enabled ? "true" : "false")}},"onPremisesSyncEnabled":false,"assignedLicenses":[]}
        """));
}
