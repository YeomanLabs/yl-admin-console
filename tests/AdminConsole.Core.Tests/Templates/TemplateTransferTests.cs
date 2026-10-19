using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Templates;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Templates;

/// <summary>Templates → Import / Export: the right kind on the right tab, nothing overwritten, and what another tenant lacks.</summary>
public sealed class TemplateTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yl-transfer-{Guid.NewGuid():N}");
    private readonly TemplateService _users;
    private readonly OffboardingProfileStore _offboarding;

    private static readonly IReadOnlyList<IOffboardingStep> Steps = [];

    public TemplateTransferTests()
    {
        var graph = new GraphClient(new HttpClient(new FakeHttpHandler()), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _users = new TemplateService(Path.Combine(_dir, "users"), graph, NullLogger<TemplateService>.Instance);
        _offboarding = new OffboardingProfileStore(Path.Combine(_dir, "offboarding"), NullLogger<OffboardingProfileStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static UserTemplate Sales => new()
    {
        Name = "Sales rep",
        Groups = [new TemplateGroup { Id = "g-here", DisplayName = "Sales" }, new TemplateGroup { Id = "g-elsewhere", DisplayName = "Contoso Sales" }],
        Licenses = [new TemplateLicense { SkuId = "sku-e3", SkuPartNumber = "SPE_E3" }, new TemplateLicense { SkuId = "sku-visio", SkuPartNumber = "VISIOCLIENT" }],
        ManagerId = "m-elsewhere",
        ManagerDisplayName = "Megan Bowen",
        LastUsed = DateTimeOffset.UtcNow,
    };

    private static OffboardingProfile Contractor => new() { Name = "Contractor", IsDefault = true };

    // ---- which file is which -----------------------------------------------------------------------------------

    [Fact]
    public void KindOf_TellsTheTwoTemplatesApart()
    {
        TemplateTransfer.KindOf(TemplateService.Export(Sales)).Should().Be(TemplateFileKind.User);
        TemplateTransfer.KindOf(OffboardingProfileStore.Export(Contractor)).Should().Be(TemplateFileKind.Offboarding);
        TemplateTransfer.KindOf("""{ "name": "who knows" }""").Should().Be(TemplateFileKind.Unknown);
        TemplateTransfer.KindOf("not json").Should().Be(TemplateFileKind.Unknown);
    }

    [Fact]
    public void Export_LeavesOutWhenItWasLastUsed_AndWhetherItIsTheDefault()
    {
        TemplateService.Export(Sales).Should().Contain("\"lastUsed\": null");
        OffboardingProfileStore.Export(Contractor).Should().Contain("\"isDefault\": false");
    }

    [Theory]
    [InlineData("Sales rep", "Sales rep (imported)")]
    [InlineData("Finance", "Finance")]
    public void UniqueName_RenamesOnlyOnAClash(string name, string expected)
    {
        TemplateTransfer.UniqueName(name, ["sales REP", "Sales rep (imported 2)"]).Should().Be(expected);
    }

    [Fact]
    public void UniqueName_CountsOnPastEarlierImports()
    {
        TemplateTransfer.UniqueName("Sales rep", ["Sales rep", "Sales rep (imported)"]).Should().Be("Sales rep (imported 2)");
    }

    // ---- user templates ------------------------------------------------------------------------------------------

    [Fact]
    public void ImportingAUserTemplate_RenamesOnAClash_AndNeverOverwritesAFile()
    {
        var first = _users.Save(Sales);
        var imported = _users.Import(TemplateService.Export(Sales), out var error);

        error.Should().BeNull();
        imported!.Name.Should().Be("Sales rep (imported)");
        imported.FileName.Should().NotBe(first.FileName);
        imported.LastUsed.Should().BeNull();
        _users.Load().Select(t => t.Name).Should().BeEquivalentTo(["Sales rep", "Sales rep (imported)"]);
    }

    [Fact]
    public void ANewUserTemplate_NoLongerReplacesOneWhoseNameMakesTheSameFile()
    {
        _users.Save(Sales with { Name = "Sales rep" });
        _users.Save(Sales with { Name = "Sales Rep!" });

        _users.Load().Should().HaveCount(2);
    }

    [Fact]
    public void AnOffboardingFileOnTheUserTab_IsRefusedWithWhereItGoes()
    {
        _users.Import(OffboardingProfileStore.Export(Contractor), out var error).Should().BeNull();
        error.Should().Be("This is an offboarding template. Import it on the Offboarding tab.");
        _users.Load().Should().BeEmpty();
    }

    [Fact]
    public void MissingHere_NamesWhatAnotherTenantDoesNotHave()
    {
        var missing = TemplateTransfer.MissingHere(Sales with { OnPrem = new TemplateOnPrem { OuDn = "OU=Sales,DC=contoso,DC=com" } },
            groupExists: id => id == "g-here",
            skuSubscribed: id => id == "sku-e3",
            userExists: _ => false);

        missing.Should().HaveCount(4);
        missing[0].Should().StartWith("1 group not in this tenant (Contoso Sales).");
        missing[1].Should().StartWith("1 licence not subscribed here (VISIOCLIENT)");
        missing[2].Should().Be("The manager (Megan Bowen) is not in this tenant; choose one in the form.");
        missing[3].Should().StartWith("It has an AD part");
    }

    [Fact]
    public void MissingHere_IsEmptyInTheTenantItCameFrom()
    {
        TemplateTransfer.MissingHere(Sales, _ => true, _ => true, _ => true).Should().BeEmpty();
    }

    // ---- offboarding templates -------------------------------------------------------------------------------------

    [Fact]
    public void ImportingAnOffboardingTemplate_RenamesOnAClash_AndIsNeverTheDefault()
    {
        _offboarding.Save(Contractor with { IsDefault = false });

        var imported = _offboarding.Import(OffboardingProfileStore.Export(Contractor), _offboarding.Load(Steps), out var error);

        error.Should().BeNull();
        imported!.Name.Should().Be("Contractor (imported)");
        imported.IsDefault.Should().BeFalse();
        _offboarding.Load(Steps).Should().HaveCount(3, "the built-in, the original and the import");
    }

    [Fact]
    public void AnOffboardingTemplateBreakingTheEditorsRules_IsRefused()
    {
        var odd = Contractor with { Options = new OffboardingOptions { AutoReplyMessage = "Ask {manager}." } };

        _offboarding.Import(OffboardingProfileStore.Export(odd), _offboarding.Load(Steps), out var error).Should().BeNull();

        error.Should().StartWith("The automatic reply has {manager}, which is not a placeholder.");
    }

    [Fact]
    public void AUserFileOnTheOffboardingTab_IsRefusedWithWhereItGoes()
    {
        _offboarding.Import(TemplateService.Export(Sales), _offboarding.Load(Steps), out var error).Should().BeNull();
        error.Should().Be("This is a user template. Import it on the New users tab.");
    }
}
