using System.IO.Compression;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Restore;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.OneDrive;
using AdminConsole.Core.SharePoint;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.OneDrive;

/// <summary>
/// M16 — OneDrive through the SharePoint admin API and Graph. The rules worth a test are SharePoint's own: a locked site
/// refuses every other change, no OneDrive is more open than the tenant, and CSOM reports failure inside a 200.
/// </summary>
public sealed class OneDriveTests : IDisposable
{
    private const string UserId = "u-leaver";
    private const string Url = "https://contoso-my.sharepoint.com/personal/adele_contoso_com";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeHttpHandler _http = new();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ac-onedrive-tests", Guid.NewGuid().ToString("N"));

    public OneDriveTests()
    {
        _tenant.Update(new TenantContextSnapshot
        {
            TenantId = "t-1",
            SignedInUpn = "admin@contoso.com",
            VerifiedDomains = ["contoso.com", "contoso.onmicrosoft.com", "contoso.mail.onmicrosoft.com"],
            GrantedScopes = [.. Scopes.Read, Scopes.SharePointFullControl, Scopes.FilesReadAll],
        });
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _actions.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private GraphClient Graph() => new(new HttpClient(_http), new FakeAuthService(), NullLogger<GraphClient>.Instance);

    private OneDriveService Service() => new(Graph(), new SharePointClient(new HttpClient(_http), new FakeAuthService(), NullLogger<SharePointClient>.Instance) { Delay = (_, _) => Task.CompletedTask },
        _tenant, _actions, NullLogger<OneDriveService>.Instance) { Delay = (_, _) => Task.CompletedTask };

    private static string Props(int sharing = 0, string lockState = "Unlock") =>
        $$"""{"SiteId":"site-1","Owner":"adele@contoso.com","StorageUsage":"38","StorageMaximumLevel":"1048576","SharingCapability":{{sharing}},"LockState":"{{lockState}}","LastContentModifiedDate":"2026-10-15T10:46:11.767"}""";

    private const string Admins = """{"value":[{"email":"Adele@contoso.com","loginName":"i:0#.f|membership|adele@contoso.com","name":"Adele Vance","userPrincipalName":"adele@contoso.com"}]}""";
    private const string Tenant = """{"SharingCapability":2,"OneDriveSharingCapability":1}""";

    /// <summary>One read of the OneDrive: properties, admins, and (the first time) the tenant's ceiling.</summary>
    private FakeHttpHandler Read(int sharing = 0, string lockState = "Unlock", string admins = Admins, bool ceiling = true)
    {
        _http.Enqueue(HttpStatusCode.OK, Props(sharing, lockState)).Enqueue(HttpStatusCode.OK, admins);
        if (ceiling) _http.Enqueue(HttpStatusCode.OK, Tenant);
        return _http;
    }

    private OneDriveDetail Detail(int sharing = 0, string lockState = "Unlock") =>
        OneDriveDetail.FromJson(UserId, Url, new SharePointHosts("contoso"), Fixtures.Parse(Props(sharing, lockState)), [], OneDriveSharing.ExternalUserSharingOnly);

    // ---- pieces --------------------------------------------------------------------------------------------------

    [Fact]
    public void QuickXorHash_matches_what_SharePoint_reported_for_a_file_uploaded_in_the_gate()
    {
        // readme.txt on the M16 test site: Graph's file.hashes.quickXorHash, 2026-10-17.
        QuickXorHash.Of(Encoding.UTF8.GetBytes("Admin Console M16 export test file A.")).Should().Be("cL5UFv/R9RgJNsfvs+O7m0ngUDM=");
    }

    [Fact]
    public void QuickXorHash_is_the_same_however_the_stream_is_cut()
    {
        var bytes = new byte[300_007];
        new Random(42).NextBytes(bytes);
        var chunked = new QuickXorHash();
        foreach (var size in new[] { 1, 159, 160, 161, 4096, 65536 })
        {
            chunked = new QuickXorHash();
            for (var i = 0; i < bytes.Length; i += size) chunked.Append(bytes.AsSpan(i, Math.Min(size, bytes.Length - i)));
            chunked.ToBase64().Should().Be(QuickXorHash.Of(bytes), $"chunks of {size} bytes");
        }
    }

    [Fact]
    public void SharePoint_is_asked_for_by_application_id_so_one_scope_fits_every_tenant()
    {
        Scopes.ResourceOf(Scopes.SharePointFullControl).Should().Be("00000003-0000-0ff1-ce00-000000000000/");
        Scopes.ShortName(Scopes.SharePointFullControl).Should().Be("AllSites.FullControl");
        Scopes.ResourceOf(Scopes.FilesReadAll).Should().BeEmpty("Files.Read.All is a Graph scope");
    }

    [Fact]
    public void The_SharePoint_name_comes_from_the_initial_domain_or_a_OneDrive_url()
    {
        SharePointHosts.FromVerifiedDomains(["contoso.com", "contoso.mail.onmicrosoft.com", "contoso.onmicrosoft.com"])!.Admin
            .Should().Be("https://contoso-admin.sharepoint.com");
        SharePointHosts.FromOneDriveUrl(Url)!.Name.Should().Be("contoso");
        SharePointHosts.FromOneDriveUrl("https://example.com/x").Should().BeNull();
    }

    [Fact]
    public void Sharing_is_ranked_by_openness_and_capped_by_the_tenant()
    {
        OneDriveSharingText.UpTo(OneDriveSharing.ExternalUserSharingOnly).Should().Equal(
            OneDriveSharing.Disabled, OneDriveSharing.ExistingExternalUserSharingOnly, OneDriveSharing.ExternalUserSharingOnly);
    }

    [Fact]
    public void Site_properties_are_read_whether_SharePoint_sends_numbers_or_strings()
    {
        var od = Detail(sharing: 1, lockState: "ReadOnly");

        od.StorageUsedMb.Should().Be(38);
        od.StorageQuotaMb.Should().Be(1048576);
        od.Sharing.Should().Be(OneDriveSharing.ExternalUserSharingOnly);
        od.IsLocked.Should().BeTrue();
        od.LastContentModified.Should().Be(new DateTimeOffset(2026, 10, 15, 10, 46, 11, 767, TimeSpan.Zero));
    }

    [Fact]
    public void A_drive_path_becomes_a_path_inside_the_zip()
    {
        OneDriveExporter.PathOf("/drives/b!x/root:/Sub%20folder", "notes été.txt").Should().Be("Sub folder/notes été.txt");
        OneDriveExporter.PathOf("/drives/b!x/root:", "readme.txt").Should().Be("readme.txt");
    }

    // ---- reads ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_user_whose_OneDrive_was_never_opened_has_none_and_SharePoint_is_not_asked()
    {
        _http.Enqueue(HttpStatusCode.OK, """{"mySite":null}""");

        (await Service().GetAsync(UserId, Ct)).Should().BeNull();
        _http.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task The_OneDrive_is_read_without_the_trailing_slash_SharePoint_rejects()
    {
        _http.Enqueue(HttpStatusCode.OK, $$"""{"mySite":"{{Url}}/"}""");
        Read();

        var od = await Service().GetAsync(UserId, Ct);

        od!.Url.Should().Be(Url);
        od.TenantCeiling.Should().Be(OneDriveSharing.ExternalUserSharingOnly, "the tenant's OneDrive setting, not its SharePoint one");
        _http.Requests[1].Uri.AbsoluteUri.Should().Be("https://contoso-admin.sharepoint.com/_api/SPO.Tenant/GetSitePropertiesByUrl");
        _http.Requests[1].Body.Should().Contain($"\"url\":\"{Url}\"");
    }

    // ---- writes --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sharing_is_refused_while_the_OneDrive_is_locked_and_nothing_is_sent()
    {
        Read(lockState: "ReadOnly");

        var outcome = await Service().SetSharingAsync(Detail(), "Adele", OneDriveSharing.ExistingExternalUserSharingOnly, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("Unlock it first");
        _http.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Post || r.Method == HttpMethod.Get, "no PATCH was attempted");
    }

    [Fact]
    public async Task Sharing_above_the_tenant_is_refused_with_where_to_change_it()
    {
        Read();

        var outcome = await Service().SetSharingAsync(Detail(), "Adele", OneDriveSharing.ExternalUserAndGuestSharing, Ct);

        outcome.FriendlyError.Should().StartWith("The tenant allows at most \"New and existing guests\"");
    }

    [Fact]
    public async Task Locking_patches_the_site_confirms_from_a_read_and_undo_unlocks()
    {
        Read().Enqueue(HttpStatusCode.NoContent);
        Read(lockState: "ReadOnly", ceiling: false);

        var outcome = await Service().SetLockStateAsync(Detail(), "Adele", OneDriveLock.ReadOnly, Ct);

        outcome.Success.Should().BeTrue();
        outcome.Warning.Should().BeNull("the read-back showed it");
        var patch = _http.Requests.Single(r => r.Method.Method == "PATCH");
        patch.Uri.AbsoluteUri.Should().Be("https://contoso-admin.sharepoint.com/_api/Microsoft.Online.SharePoint.TenantAdministration.Tenant/Sites('site-1')");
        patch.Body.Should().Be("""{"LockState":"ReadOnly"}""");
        _actions.Get(outcome.ActionId)!.InverseAction.Should().Be("onedrive.lock");
        outcome.CanUndo.Should().BeTrue();
    }

    [Fact]
    public async Task A_site_admin_is_added_through_CSOM_and_a_CSOM_error_inside_a_200_is_a_failure()
    {
        Read().Enqueue(HttpStatusCode.OK, """
            [{"SchemaVersion":"15.0.0.0","ErrorInfo":{"ErrorMessage":"The user does not exist or is not unique.","ErrorCode":-2130575276,"ErrorTypeName":"Microsoft.SharePoint.SPException"}}]
            """);

        var outcome = await Service().SetAdminAsync(Detail(), "Adele", "megan@contoso.com", "Megan", admin: true, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("SharePoint doesn't know that person");
        var csom = _http.Requests.Last();
        csom.Uri.AbsoluteUri.Should().Be("https://contoso-admin.sharepoint.com/_vti_bin/client.svc/ProcessQuery");
        csom.Body.Should().Contain("<Parameter Type=\"String\">i:0#.f|membership|megan@contoso.com</Parameter>")
            .And.Contain("TypeId=\"{268004ae-ef6b-4e9b-8425-127220d84719}\"");
    }

    [Fact]
    public async Task The_owner_is_never_taken_off_the_admins()
    {
        Read();

        var outcome = await Service().SetAdminAsync(Detail(), "Adele", "adele@contoso.com", "Adele", admin: false, Ct);

        outcome.FriendlyError.Should().Be("Adele owns this OneDrive, so they stay its admin.");
    }

    // ---- offboarding and restore ---------------------------------------------------------------------------------

    private OffboardingContext Context(PersonRef? delegateTo = null, OffboardingSnapshot? restoreFrom = null) => new()
    {
        User = new DirectoryUser { Id = UserId, UserPrincipalName = "adele@contoso.com", DisplayName = "Adele", AccountEnabled = true, OnPremisesSyncEnabled = false, Licensed = true, Raw = "{}" },
        Tenant = _tenant,
        Delegate = delegateTo,
        CorrelationId = "run-1",
        RestoreFrom = restoreFrom,
    };

    [Fact]
    public async Task Delegating_the_OneDrive_needs_a_delegate_and_says_what_it_is_for()
    {
        _http.Enqueue(HttpStatusCode.OK, $$"""{"mySite":"{{Url}}"}""");
        Read();
        var step = new DelegateOneDriveStep(Service());

        (await step.PreviewAsync(Context(), Ct)).Reason.Should().StartWith("Choose a delegate first");

        _http.Enqueue(HttpStatusCode.OK, $$"""{"mySite":"{{Url}}"}""");
        Read();
        var preview = await new DelegateOneDriveStep(Service()).PreviewAsync(Context(new PersonRef("u-megan", "Megan Bowen", "megan@contoso.com")), Ct);
        preview.CanRun.Should().BeTrue();
        preview.Summary.Should().Be("Make Megan Bowen an admin of the OneDrive (38 MB)");
    }

    [Fact]
    public async Task Restore_unlocks_first_then_takes_off_the_admin_added_since()
    {
        var snapshot = new OffboardingSnapshot
        {
            TenantId = "t-1", UserId = UserId, UserPrincipalName = "adele@contoso.com", TakenAt = DateTimeOffset.UtcNow,
            OneDrive = new SnapshotOneDrive { Url = Url, LockState = "Unlock", Sharing = 0, Admins = ["adele@contoso.com"] },
        };
        _http.Enqueue(HttpStatusCode.OK, $$"""{"mySite":"{{Url}}"}""");
        Read(lockState: "ReadOnly", admins: """{"value":[{"loginName":"i:0#.f|membership|adele@contoso.com","name":"Adele","userPrincipalName":"adele@contoso.com"},{"loginName":"i:0#.f|membership|megan@contoso.com","name":"Megan Bowen","userPrincipalName":"megan@contoso.com"}]}""");
        var step = RestoreCatalog.Build(null!, null!, null!, Graph(), _actions, _tenant, new Core.OnPrem.FakeDirectoryService(), null!, Service()).Single(s => s.Id == "restore-onedrive");

        var preview = await step.PreviewAsync(Context(restoreFrom: snapshot), Ct);

        preview.Details.Should().Equal("Set the lock back to unlocked", "Take Megan Bowen (megan@contoso.com) off the admins, added since the snapshot");
    }

    // ---- export --------------------------------------------------------------------------------------------------

    private static HttpResponseMessage Bytes(string text) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    private static string Delta(params (string Id, string Parent, string Name, string Text)[] files) =>
        "{\"value\":[{\"id\":\"root\",\"name\":\"root\",\"folder\":{}}" + string.Concat(files.Select(f => "," +
            $"{{\"id\":\"{f.Id}\",\"name\":\"{f.Name}\",\"size\":{Encoding.UTF8.GetByteCount(f.Text)},\"parentReference\":{{\"path\":\"{f.Parent}\"}}," +
            $"\"file\":{{\"hashes\":{{\"quickXorHash\":\"{QuickXorHash.Of(Encoding.UTF8.GetBytes(f.Text))}\"}}}},\"lastModifiedDateTime\":\"2026-10-17T18:12:06Z\"}}")) +
        "],\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/drives/d1/root/delta?token=x\"}";

    private OneDriveExporter Exporter() => new(Graph(), Service(), new FakeAuthService(), new HttpClient(_http), _tenant, _actions, NullLogger<OneDriveExporter>.Instance)
    {
        Delay = (_, _) => Task.CompletedTask,
    };

    private OneDriveDetail MineAlready() => Detail() with
    {
        Admins = [new SiteAdmin { LoginName = "i:0#.f|membership|admin@contoso.com", UserPrincipalName = "admin@contoso.com" }],
    };

    [Fact]
    public async Task An_export_streams_every_file_checks_its_hash_and_writes_a_zip_with_the_folders()
    {
        var zip = Path.Combine(_dir, "adele.zip");
        _http.Enqueue(HttpStatusCode.OK, """{"id":"d1"}""")
            .Enqueue(HttpStatusCode.OK, Delta(("f1", "/drives/d1/root:", "readme.txt", "Admin Console M16 export test file A."),
                                              ("f2", "/drives/d1/root:/Sub%20folder", "notes.txt", "Second file")))
            .Enqueue(_ => Bytes("Admin Console M16 export test file A."))
            .Enqueue(_ => Bytes("Second file"));

        var result = await Exporter().ExportAsync(MineAlready(), "Adele", zip, null, Ct);

        result.Complete.Should().BeTrue();
        result.Verified.Should().Be(2);
        using var archive = ZipFile.OpenRead(zip);
        archive.Entries.Select(e => e.FullName).Should().Equal("readme.txt", "Sub folder/notes.txt");
        using (var reader = new StreamReader(archive.GetEntry("Sub folder/notes.txt")!.Open())) (await reader.ReadToEndAsync()).Should().Be("Second file");
        Directory.Exists(OneDriveExporter.PartsFolder(zip)).Should().BeFalse("the parts are removed once the ZIP is whole");
        _actions.Query(new ActionLogQuery { ActionPrefix = "onedrive.export" }).Should().ContainSingle();
    }

    [Fact]
    public async Task A_file_that_arrives_corrupted_twice_fails_the_export_and_no_zip_is_written()
    {
        var zip = Path.Combine(_dir, "bad.zip");
        _http.Enqueue(HttpStatusCode.OK, """{"id":"d1"}""")
            .Enqueue(HttpStatusCode.OK, Delta(("f1", "/drives/d1/root:", "readme.txt", "Admin Console M16 export test file A.")))
            .Enqueue(_ => Bytes("Admin Console M16 export test file B."))
            .Enqueue(_ => Bytes("Admin Console M16 export test file C."));

        var result = await Exporter().ExportAsync(MineAlready(), "Adele", zip, null, Ct);

        result.Complete.Should().BeFalse();
        result.Failed.Should().Equal("readme.txt");
        File.Exists(zip).Should().BeFalse("a ZIP that exists is a complete one");
    }

    [Fact]
    public async Task An_interrupted_export_keeps_the_files_already_down_and_fetches_only_the_rest()
    {
        var zip = Path.Combine(_dir, "resume.zip");
        var parts = OneDriveExporter.PartsFolder(zip);
        Directory.CreateDirectory(parts);
        File.WriteAllText(Path.Combine(parts, "000000.bin"), "Admin Console M16 export test file A.");
        _http.Enqueue(HttpStatusCode.OK, """{"id":"d1"}""")
            .Enqueue(HttpStatusCode.OK, Delta(("f1", "/drives/d1/root:", "readme.txt", "Admin Console M16 export test file A."),
                                              ("f2", "/drives/d1/root:", "second.txt", "Second file")))
            .Enqueue(_ => Bytes("Second file"));

        var result = await Exporter().ExportAsync(MineAlready(), "Adele", zip, null, Ct);

        result.Complete.Should().BeTrue();
        result.Resumed.Should().Be(1);
        _http.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task An_admin_who_cannot_open_the_OneDrive_is_made_an_admin_for_the_export_and_taken_off_afterwards()
    {
        var zip = Path.Combine(_dir, "grant.zip");
        const string WithMe = """{"value":[{"loginName":"i:0#.f|membership|adele@contoso.com","userPrincipalName":"adele@contoso.com"},{"loginName":"i:0#.f|membership|admin@contoso.com","userPrincipalName":"admin@contoso.com"}]}""";
        const string Ok = """[{"SchemaVersion":"15.0.0.0","ErrorInfo":null},2,{}]""";
        Read().Enqueue(HttpStatusCode.OK, Ok);                    // grant: read, CSOM
        Read(admins: WithMe, ceiling: false);                      // grant confirmed
        _http.Enqueue(HttpStatusCode.OK, """{"id":"d1"}""").Enqueue(HttpStatusCode.OK, Delta());
        Read(admins: WithMe, ceiling: false).Enqueue(HttpStatusCode.OK, Ok);   // take off: read, CSOM
        Read(ceiling: false);                                      // take-off confirmed

        var result = await Exporter().ExportAsync(Detail(), "Adele", zip, null, Ct);

        result.Complete.Should().BeTrue();
        var csom = _http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("ProcessQuery", StringComparison.Ordinal)).ToList();
        csom.Should().HaveCount(2);
        csom[0].Body.Should().Contain("<Parameter Type=\"Boolean\">true</Parameter>");
        csom[1].Body.Should().Contain("<Parameter Type=\"Boolean\">false</Parameter>");
    }
}
