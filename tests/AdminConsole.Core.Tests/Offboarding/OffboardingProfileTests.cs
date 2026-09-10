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

        _store.LoadErrors.Should().ContainSingle().Which.Should().Be("nameless.json — no profile name");
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
