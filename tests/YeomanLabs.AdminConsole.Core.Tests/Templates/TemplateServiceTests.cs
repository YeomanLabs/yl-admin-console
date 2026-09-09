using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Templates;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.Core.Tests.Templates;

/// <summary>M7.1: snapshot rules, the on-disk store, and the password guarantees doc 07 asks for.</summary>
public sealed class TemplateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yl-templates-{Guid.NewGuid():N}");
    private readonly FakeHttpHandler _handler = new();
    private readonly TemplateService _sut;

    public TemplateServiceTests()
    {
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new TemplateService(_dir, graph, NullLogger<TemplateService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static UserTemplate Template(string name = "Sales Rep — Madison") => new()
    {
        Name = name,
        Attributes = new Dictionary<string, object?> { ["jobTitle"] = "Sales Representative", ["usageLocation"] = "US" },
        Licenses = [new TemplateLicense { SkuId = "sku-1", SkuPartNumber = "SPE_E3" }],
        Groups = [new TemplateGroup { Id = "g-1", DisplayName = "Sales-Team" }],
    };

    // ---- the store --------------------------------------------------------------------------------------------------

    [Fact]
    public void SaveThenLoad_RoundTripsEverythingTheFileHolds()
    {
        var saved = _sut.Save(Template());

        saved.FileName.Should().Be("sales-rep-madison.json");

        var loaded = _sut.Load().Should().ContainSingle().Subject;
        loaded.Name.Should().Be("Sales Rep — Madison");
        loaded.Attributes["jobTitle"].ToString().Should().Be("Sales Representative");
        loaded.Licenses.Single().SkuPartNumber.Should().Be("SPE_E3");
        loaded.Groups.Single().DisplayName.Should().Be("Sales-Team");
        loaded.SchemaVersion.Should().Be(UserTemplate.CurrentSchemaVersion);
        loaded.IsSaved.Should().BeTrue();
    }

    [Fact]
    public void Load_WithNoDirectoryYet_IsEmptyRatherThanAnError()
    {
        _sut.Load().Should().BeEmpty();
        _sut.LoadErrors.Should().BeEmpty();
    }

    [Fact]
    public void Load_AFileThatWillNotParse_IsReported_NotSilentlyDropped()
    {
        // A template that quietly vanishes is how someone creates an account with the wrong licences.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ nope");
        _sut.Save(Template());

        _sut.Load().Should().ContainSingle();
        _sut.LoadErrors.Should().ContainSingle().Which.Should().Be("broken.json — not valid JSON");
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var saved = _sut.Save(Template());

        _sut.Delete(saved);

        _sut.Load().Should().BeEmpty();
    }

    [Fact]
    public void MarkUsed_StampsTheDate_SoThePickerCanOrderByIt()
    {
        var saved = _sut.Save(Template());

        var used = _sut.MarkUsed(saved);

        used.LastUsed.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        _sut.Load().Single().LastUsed.Should().NotBeNull();
    }

    // ---- the editor -------------------------------------------------------------------------------------------------

    [Fact]
    public void TryParse_GoodJson_RoundTripsWhatSerializeWrote()
    {
        var json = TemplateService.Serialize(Template());

        var parsed = TemplateService.TryParse(json, out var error);

        error.Should().BeNull();
        parsed!.Name.Should().Be("Sales Rep — Madison");
    }

    [Theory]
    [InlineData("{ not json", "not valid JSON")]
    [InlineData("""{"attributes":{}}""", "no template name")]
    [InlineData("""{"name":"X","passwordPolicy":{"length":4}}""", "The password length must be at least 8.")]
    public void TryParse_BadJson_ExplainsWhatIsWrong_WithoutThrowingAtTheEditor(string json, string expected)
    {
        var parsed = TemplateService.TryParse(json, out var error);

        parsed.Should().BeNull();
        error.Should().Be(expected);
    }

    // ---- snapshot ---------------------------------------------------------------------------------------------------

    private void EnqueueSnapshot(string userJson, string licenseStatesJson, string memberOfJson, string? managerJson)
    {
        _handler.Enqueue(HttpStatusCode.OK, userJson)
                .Enqueue(HttpStatusCode.OK, licenseStatesJson)
                .Enqueue(HttpStatusCode.OK, memberOfJson);
        if (managerJson is null) _handler.Enqueue(HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound","message":"not found"}}""");
        else _handler.Enqueue(HttpStatusCode.OK, managerJson);
    }

    private const string SourceUser = """
        {"id":"u-1","displayName":"Jane Doe","userPrincipalName":"jane.doe@contoso.com","mail":"jane.doe@contoso.com",
         "givenName":"Jane","surname":"Doe","mobilePhone":"+1 555 0199","employeeId":"E-4417","accountEnabled":true,
         "jobTitle":"Sales Representative","department":"Sales","companyName":"Contoso","officeLocation":"Madison",
         "city":"Madison","state":"WI","postalCode":"53703","country":"US","usageLocation":"US","preferredLanguage":"en-US",
         "employeeType":"Employee","businessPhones":["+1 608 555 0100"],"assignedLicenses":[]}
        """;

    [Fact]
    public async Task Snapshot_CopiesTheRoleAttributes_AndNoneOfThePersonalOnes()
    {
        EnqueueSnapshot(SourceUser, """{"licenseAssignmentStates":[]}""", """{"value":[]}""", null);

        var template = await _sut.SnapshotAsync("Sales Rep — Madison", "u-1", CancellationToken.None);

        template.Attributes.Keys.Should().BeEquivalentTo([
            "jobTitle", "department", "companyName", "officeLocation",
            "city", "state", "postalCode", "country",
            "usageLocation", "preferredLanguage", "employeeType", "businessPhones",
        ]);
        template.Attributes.Should().NotContainKeys("displayName", "givenName", "surname", "userPrincipalName", "mail", "mobilePhone", "employeeId");
        template.CreatedFrom!.UserId.Should().Be("u-1");
        template.CreatedFrom.Upn.Should().Be("jane.doe@contoso.com");
    }

    [Fact]
    public async Task Snapshot_TakesDirectLicencesOnly_AndSaysWhatItLeftOut()
    {
        EnqueueSnapshot(SourceUser, """
            {"licenseAssignmentStates":[
              {"skuId":"sku-direct","assignedByGroup":null,"state":"Active","disabledPlans":["plan-1"]},
              {"skuId":"sku-group","assignedByGroup":"grp-1","state":"Active"}]}
            """, """{"value":[]}""", null);

        var template = await _sut.SnapshotAsync("Sales", "u-1", CancellationToken.None);

        template.Licenses.Should().ContainSingle().Which.SkuId.Should().Be("sku-direct");
        template.Licenses.Single().DisabledPlans.Should().Equal(["plan-1"]);
        template.ExcludedOnSnapshot.Should().ContainSingle().Which.Should().Contain("1 group-assigned licence");
    }

    [Fact]
    public async Task Snapshot_TakesCloudGroupsOnly_AndNamesEachOneItSkipped()
    {
        EnqueueSnapshot(SourceUser, """{"licenseAssignmentStates":[]}""", """
            {"value":[
              {"id":"g-ok","displayName":"Sales-Team","securityEnabled":true,"groupTypes":[]},
              {"id":"g-dyn","displayName":"All-Staff","securityEnabled":true,"groupTypes":["DynamicMembership"]},
              {"id":"g-sync","displayName":"Finance","securityEnabled":true,"groupTypes":[],"onPremisesSyncEnabled":true},
              {"id":"g-role","displayName":"Admins","securityEnabled":true,"groupTypes":[],"isAssignableToRole":true}]}
            """, null);

        var template = await _sut.SnapshotAsync("Sales", "u-1", CancellationToken.None);

        template.Groups.Should().ContainSingle().Which.DisplayName.Should().Be("Sales-Team");
        template.ExcludedOnSnapshot.Should().HaveCount(3);
        template.ExcludedOnSnapshot.Should().Contain(n => n.StartsWith("All-Staff") && n.Contains("Dynamic"));
        template.ExcludedOnSnapshot.Should().Contain(n => n.StartsWith("Finance") && n.Contains("on-prem"));
        template.ExcludedOnSnapshot.Should().Contain(n => n.StartsWith("Admins") && n.Contains("Privileged Role Administrator"));
    }

    [Fact]
    public async Task Snapshot_KeepsTheManagerByIdWithTheNameCachedForDisplay()
    {
        EnqueueSnapshot(SourceUser, """{"licenseAssignmentStates":[]}""", """{"value":[]}""",
            """{"id":"m-1","displayName":"Sam Smith","userPrincipalName":"sam@contoso.com"}""");

        var template = await _sut.SnapshotAsync("Sales", "u-1", CancellationToken.None);

        template.ManagerId.Should().Be("m-1");
        template.ManagerDisplayName.Should().Be("Sam Smith");
    }

    [Fact]
    public async Task Snapshot_WithNoManager_LeavesItUnset_RatherThanFailing()
    {
        EnqueueSnapshot(SourceUser, """{"licenseAssignmentStates":[]}""", """{"value":[]}""", null);

        var template = await _sut.SnapshotAsync("Sales", "u-1", CancellationToken.None);

        template.ManagerId.Should().BeNull();
    }

    // ---- passwords (doc 07 §Password generation) ---------------------------------------------------------------------

    [Fact]
    public void PasswordPolicy_DefaultsToDoc07sSixteenCharacters_ChangedOnFirstSignIn()
    {
        var policy = new PasswordPolicy();

        policy.Length.Should().Be(16);
        policy.ForceChangeOnFirstSignIn.Should().BeTrue();
    }

    [Fact]
    public void GeneratedPasswords_HonourTheTemplateLength_AndKeepTheClassGuarantees()
    {
        const string symbols = "!@#$%^&*-_=+?";
        var samples = Enumerable.Range(0, 100).Select(_ => UserService.GeneratePassword(new PasswordPolicy().Length)).ToList();

        samples.Should().OnlyContain(p => p.Length == 16);
        samples.Should().OnlyContain(p => p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit) && p.Any(symbols.Contains));
        samples.Should().OnlyContain(p => !p.Any(c => "0O1lI|".Contains(c)), "doc 07 names these glyphs as too easy to misread");
        samples.Distinct().Should().HaveCount(samples.Count);
    }
}
