using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Offboarding;

namespace AdminConsole.Core.Tests.Offboarding;

/// <summary>M6.3: profiles on disk, and the built-in "Standard leaver".</summary>
public sealed class OffboardingProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yl-profiles-{Guid.NewGuid():N}");
    private readonly OffboardingProfileStore _store;

    public OffboardingProfileTests() => _store = new OffboardingProfileStore(_dir, NullLogger<OffboardingProfileStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly IReadOnlyList<IOffboardingStep> Steps =
    [
        new StubStep("disable-account", defaultEnabled: true),
        new StubStep("retire-devices", defaultEnabled: false),
        new StubStep("remove-licenses", defaultEnabled: true),
    ];

    [Fact]
    public void StandardLeaver_IsTheCatalogsOwnDefaults()
    {
        var profile = OffboardingProfile.StandardLeaver(Steps);

        profile.Name.Should().Be("Standard leaver");
        profile.IsBuiltIn.Should().BeTrue();
        profile.EnabledIds(Steps).Should().Equal(["disable-account", "remove-licenses"]);
    }

    [Fact]
    public void AProfileThatDoesNotMentionAStep_FallsBackToThatStepsOwnDefault()
    {
        // A profile saved before a step existed must still work, and the safe reading is the catalog's default.
        var profile = new OffboardingProfile { Name = "Partial", Steps = new Dictionary<string, bool> { ["retire-devices"] = true } };

        profile.EnabledIds(Steps).Should().Equal(["disable-account", "retire-devices", "remove-licenses"]);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheTogglesAndTheOptions()
    {
        var saved = _store.Save(OffboardingProfile.From("Contractor leaver", Steps, ["disable-account"], new OffboardingOptions
        {
            DelegateToManager = false,
            AttributeStamps = new Dictionary<string, string?> { ["employeeType"] = "Contractor (left)" },
        }));

        saved.FileName.Should().Be("contractor-leaver.json");

        var loaded = _store.Load(Steps).Single(p => !p.IsBuiltIn);

        loaded.Name.Should().Be("Contractor leaver");
        loaded.EnabledIds(Steps).Should().Equal(["disable-account"]);
        loaded.Options.DelegateToManager.Should().BeFalse();
        loaded.Options.AttributeStamps["employeeType"].Should().Be("Contractor (left)");
        loaded.IsBuiltIn.Should().BeFalse();
    }

    [Fact]
    public void Load_AlwaysPutsTheBuiltInDefaultFirst()
    {
        _store.Save(OffboardingProfile.From("Aardvark", Steps, [], new OffboardingOptions()));

        var profiles = _store.Load(Steps);

        profiles[0].Name.Should().Be("Standard leaver");
        profiles.Should().HaveCount(2);
    }

    [Fact]
    public void Load_WithNoDirectoryYet_StillOffersTheBuiltInDefault()
    {
        _store.Load(Steps).Should().ContainSingle().Which.IsBuiltIn.Should().BeTrue();
    }

    [Fact]
    public void Load_AFileThatWillNotParse_IsReported_NotSilentlyDropped()
    {
        // A profile that quietly vanishes is how an admin ends up running the wrong set of steps.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ this is not json");

        var profiles = _store.Load(Steps);

        profiles.Should().ContainSingle().Which.IsBuiltIn.Should().BeTrue();
        _store.LoadErrors.Should().ContainSingle().Which.Should().Be("broken.json — not valid JSON");
    }

    [Fact]
    public void Load_AFileWithNoName_IsReportedInWordsAnAdminCanActOn()
    {
        // System.Text.Json describes this by naming the CLR type, which tells the reader nothing about their file.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "nameless.json"), """{"steps":{}}""");

        _store.Load(Steps);

        _store.LoadErrors.Should().ContainSingle().Which.Should().Be("nameless.json — no template name");
    }

    [Fact]
    public void Save_TwiceUnderTheSameName_OverwritesRatherThanPilingUpFiles()
    {
        var first = _store.Save(OffboardingProfile.From("Standard leaver plus", Steps, ["disable-account"], new OffboardingOptions()));
        _store.Save(first with { Steps = new Dictionary<string, bool> { ["disable-account"] = false } });

        var loaded = _store.Load(Steps).Single(p => !p.IsBuiltIn);

        Directory.GetFiles(_dir, "*.json").Should().ContainSingle();
        loaded.IsEnabled(Steps[0]).Should().BeFalse();
    }

    [Fact]
    public void Delete_RemovesASavedProfile_AndIgnoresTheBuiltIn()
    {
        var saved = _store.Save(OffboardingProfile.From("Temp", Steps, [], new OffboardingOptions()));

        _store.Delete(OffboardingProfile.StandardLeaver(Steps));
        Directory.GetFiles(_dir, "*.json").Should().ContainSingle("the built-in has no file to delete");

        _store.Delete(saved);
        _store.Load(Steps).Should().ContainSingle().Which.IsBuiltIn.Should().BeTrue();
    }

    // ---- offboarding templates on the Templates page ------------------------------------------------------------

    [Fact]
    public void SaveThenLoad_RoundTripsTheDescriptionAndTheDefaultMark()
    {
        _store.Save(OffboardingProfile.From("Contractor", Steps, [], new OffboardingOptions()) with { Description = "Keeps the mailbox", IsDefault = true });

        var loaded = _store.Load(Steps).Single(p => !p.IsBuiltIn);

        loaded.Description.Should().Be("Keeps the mailbox");
        loaded.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Save_ANewTemplateWhoseNameMakesTheSameFileName_GetsAFileOfItsOwn()
    {
        // "Contractor!" and "Contractor" make the same slug; the second must not quietly replace the first.
        var first = _store.Save(OffboardingProfile.From("Contractor", Steps, [], new OffboardingOptions()));
        var second = _store.Save(OffboardingProfile.From("Contractor!", Steps, [], new OffboardingOptions()));

        first.FileName.Should().Be("contractor.json");
        second.FileName.Should().Be("contractor-2.json");
        _store.Load(Steps).Select(p => p.Name).Should().Equal("Standard leaver", "Contractor", "Contractor!");
    }

    [Fact]
    public void Save_ARenamedTemplate_KeepsItsFile()
    {
        var saved = _store.Save(OffboardingProfile.From("Contractor", Steps, [], new OffboardingOptions()));

        var renamed = _store.Save(saved with { Name = "Contractor leaver" });

        renamed.FileName.Should().Be("contractor.json");
        Directory.GetFiles(_dir, "*.json").Should().ContainSingle();
    }

    [Fact]
    public void DefaultOf_IsTheMarkedTemplate_OrTheBuiltInWhenNoneIs()
    {
        _store.Save(OffboardingProfile.From("A", Steps, [], new OffboardingOptions()));
        OffboardingProfileStore.DefaultOf(_store.Load(Steps)).IsBuiltIn.Should().BeTrue();

        _store.Save(OffboardingProfile.From("B", Steps, [], new OffboardingOptions()) with { IsDefault = true });
        OffboardingProfileStore.DefaultOf(_store.Load(Steps)).Name.Should().Be("B");
    }

    [Fact]
    public void SetDefault_LeavesExactlyOneMarked_AndTheBuiltInClearsThemAll()
    {
        _store.Save(OffboardingProfile.From("A", Steps, [], new OffboardingOptions()) with { IsDefault = true });
        _store.Save(OffboardingProfile.From("B", Steps, [], new OffboardingOptions()));
        var profiles = _store.Load(Steps);

        _store.SetDefault(profiles, profiles.Single(p => p.Name == "B"));
        _store.Load(Steps).Where(p => p.IsDefault).Select(p => p.Name).Should().Equal("B");

        _store.SetDefault(_store.Load(Steps), _store.Load(Steps)[0]);
        _store.Load(Steps).Should().NotContain(p => p.IsDefault);
    }

    [Fact]
    public void Problems_NoneForAGoodTemplate()
    {
        var profile = OffboardingProfile.From("Contractor", Steps, [], new OffboardingOptions
        {
            AttributeStamps = new Dictionary<string, string?> { ["employeeType"] = "Left", ["companyName"] = "" },
            AutoReplyMessage = "{name} has left {company}; write to {delegate}.",
        });

        profile.Problems(_store.Load(Steps)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("  ", "Give the template a name.")]
    [InlineData("standard LEAVER", "“Standard leaver” is the built-in template. Choose another name.")]
    [InlineData("Contractor", "A template called “Contractor” already exists.")]
    public void Problems_NamesAClashOrAMissingName(string name, string expected)
    {
        _store.Save(OffboardingProfile.From("Contractor", Steps, [], new OffboardingOptions()));

        var profile = OffboardingProfile.From(name, Steps, [], new OffboardingOptions());

        profile.Problems(_store.Load(Steps)).Should().Equal(expected);
    }

    [Fact]
    public void Problems_ATemplateIsNoClashWithItsOwnSavedCopy()
    {
        var saved = _store.Save(OffboardingProfile.From("Contractor", Steps, [], new OffboardingOptions()));

        (saved with { Name = "contractor" }).Problems(_store.Load(Steps)).Should().BeEmpty();
    }

    [Fact]
    public void Problems_RefusesAStampRestoreCouldNotPutBack()
    {
        var profile = OffboardingProfile.From("Odd", Steps, [], new OffboardingOptions
        {
            AttributeStamps = new Dictionary<string, string?> { ["usageLocation"] = "US", ["employeeType"] = "Left" },
        });

        profile.Problems([]).Should().ContainSingle().Which.Should().EndWith("Not: usageLocation.");
    }

    [Fact]
    public void Problems_RefusesAPlaceholderThatDoesNotExist()
    {
        var profile = OffboardingProfile.From("Odd", Steps, [], new OffboardingOptions { AutoReplyMessage = "Ask {manager}." });

        profile.Problems([]).Should().ContainSingle().Which.Should().StartWith("The automatic reply has {manager}, which is not a placeholder.");
    }

    [Theory]
    [InlineData("Standard leaver", "standard-leaver")]
    [InlineData("  Contractor / Temp  ", "contractor-temp")]
    [InlineData("!!!", "untitled")]
    [InlineData("Årsavslutning", "årsavslutning")]
    public void Slug_MakesAFileNameThatNeedsNoEscaping(string name, string expected)
    {
        OffboardingProfileStore.Slug(name).Should().Be(expected);
    }

    private sealed class StubStep : IOffboardingStep
    {
        public StubStep(string id, bool defaultEnabled)
        {
            Id = id;
            DefaultEnabled = defaultEnabled;
        }

        public string Id { get; }
        public string Title => Id;
        public StepSurface Surface => StepSurface.Graph;
        public Capability? RequiredCapability => null;
        public bool DefaultEnabled { get; }
        public bool HaltsOnFailure => false;

        public Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct) =>
            Task.FromResult(new StepPreview { StepId = Id, Summary = Id });

        public Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct) =>
            Task.FromResult(StepResult.Success(Id, Id));
    }
}
