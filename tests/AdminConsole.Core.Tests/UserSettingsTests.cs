using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core;

namespace AdminConsole.Core.Tests;

/// <summary>M8.1: the settings an admin chooses, kept beside the cache rather than in the shipped config file.</summary>
public sealed class UserSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"yl-settings-{Guid.NewGuid():N}", "settings.json");
    private readonly UserSettingsStore _store;

    public UserSettingsTests() => _store = new UserSettingsStore(_path, NullLogger<UserSettingsStore>.Instance);

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Load_WithNoFileYet_GivesTheDocumentedDefaults()
    {
        var settings = _store.Load();

        settings.DeltaIntervalSeconds.Should().Be(120);
        settings.DeviceRefreshIntervalSeconds.Should().Be(600);
        settings.Theme.Should().Be(AppTheme.Dark);
        settings.Density.Should().Be(AppDensity.Comfortable);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryChoice()
    {
        _store.Save(new UserSettings
        {
            DeltaIntervalSeconds = 300,
            DeviceRefreshIntervalSeconds = 1800,
            Theme = AppTheme.Light,
            Density = AppDensity.Compact,
        });

        var settings = _store.Load();

        settings.DeltaIntervalSeconds.Should().Be(300);
        settings.DeviceRefreshIntervalSeconds.Should().Be(1800);
        settings.Theme.Should().Be(AppTheme.Light);
        settings.Density.Should().Be(AppDensity.Compact);
    }

    [Fact]
    public void Save_WritesEnumsAsNames_SoTheFileCanBeEditedByHand()
    {
        // "theme": 2 tells a reader nothing, and a hand-typed "Light" against a numeric file fails to parse and is
        // silently replaced by the default — which is exactly how the M8.4 light-theme check looked like it passed.
        _store.Save(new UserSettings { Theme = AppTheme.Light, Density = AppDensity.Compact });

        var json = File.ReadAllText(_path);

        json.Should().Contain("\"Light\"").And.Contain("\"Compact\"");
        json.Should().NotContain("\"theme\": 2");
    }

    [Fact]
    public void Load_ReadsAHandTypedEnumName()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """{"deltaIntervalSeconds":120,"deviceRefreshIntervalSeconds":600,"theme":"Light","density":"Compact"}""");

        var settings = _store.Load();

        settings.Theme.Should().Be(AppTheme.Light);
        settings.Density.Should().Be(AppDensity.Compact);
    }

    [Fact]
    public void Load_ABrokenFile_FallsBackToDefaults_RatherThanFailingToStart()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");

        _store.Load().DeltaIntervalSeconds.Should().Be(120);
    }

    [Theory]
    [InlineData(30, "30 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(120, "2 minutes")]
    [InlineData(1800, "30 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(7200, "2 hours")]
    public void Describe_ReadsAsTime_NotAsANumber(int seconds, string expected)
    {
        UserSettings.Describe(seconds).Should().Be(expected);
    }

    [Fact]
    public void TheOfferedIntervals_AreNeverFasterThanAMinute()
    {
        // Anything quicker just burns Graph quota for a directory that has not changed.
        UserSettings.DeltaChoices.Should().OnlyContain(c => c >= 60);
        UserSettings.DeviceRefreshChoices.Should().OnlyContain(c => c >= 300);
    }
}
