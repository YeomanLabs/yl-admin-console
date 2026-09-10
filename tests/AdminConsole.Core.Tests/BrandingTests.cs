using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core;

namespace AdminConsole.Core.Tests;

/// <summary>M11.3: what an installation calls itself and what it looks like.</summary>
public sealed class BrandingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yl-brand-{Guid.NewGuid():N}");
    private readonly string _path;

    public BrandingTests() => _path = Path.Combine(_dir, "branding.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private BrandingStore Store() => new(_path, NullLogger<BrandingStore>.Instance);

    [Fact]
    public void TheDefaultIsNobodyInParticular()
    {
        var branding = new Branding();

        branding.ProductName.Should().Be("Admin Console");
        branding.IsDefault.Should().BeTrue();
        foreach (var value in new[] { branding.ProductName, branding.Tagline, branding.ChromeColor, branding.AccentColor })
        {
            value.Should().NotContain("YeomanLabs", "the product ships branded to no one");
        }
    }

    [Fact]
    public void Load_WithNoFile_IsTheDefault()
    {
        Store().Load().IsDefault.Should().BeTrue();
    }

    [Fact]
    public void SaveThenLoad_KeepsWhatTheInstallationChose()
    {
        Store().Save(new Branding
        {
            ProductName = "Contoso IT Console",
            Tagline = "Contoso identity and devices",
            ChromeColor = "#2B1B4D",
            AccentColor = "#C08CFF",
        });

        var loaded = Store().Load();

        loaded.ProductName.Should().Be("Contoso IT Console");
        loaded.ChromeColor.Should().Be("#2B1B4D");
        loaded.AccentColor.Should().Be("#C08CFF");
        loaded.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void AColourThatCannotBePainted_FallsBackRatherThanBreakingTheWindow()
    {
        // Hand-edited file, so this is a matter of when rather than if. An unpaintable value must not produce an
        // unreadable window that the person who typed it cannot get out of.
        var sanitised = new Branding { AccentColor = "octarine", ChromeColor = "#GGGGGG" }.Sanitised();

        sanitised.AccentColor.Should().Be(new Branding().AccentColor);
        sanitised.ChromeColor.Should().Be(new Branding().ChromeColor);
    }

    [Fact]
    public void AnEmptyProductName_FallsBackToTheDefault()
    {
        new Branding { ProductName = "   " }.Sanitised().ProductName.Should().Be("Admin Console");
    }

    [Fact]
    public void TheProductNameIsTrimmed_BecauseItArrivesPasted()
    {
        new Branding { ProductName = "  Contoso IT Console \r\n" }.Sanitised().ProductName.Should().Be("Contoso IT Console");
    }

    [Theory]
    [InlineData("#58A6FF", true)]
    [InlineData("#fff", true)]
    [InlineData("58A6FF", false)]
    [InlineData("#58A6F", false)]
    [InlineData("#ZZZZZZ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeColor_AcceptsOnlyWhatCanBePainted(string? value, bool expected)
    {
        Branding.LooksLikeColor(value).Should().Be(expected);
    }

    [Fact]
    public void Load_ABrokenFile_FallsBackRatherThanFailingToStart()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "{ not json");

        Store().Load().IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Reset_GoesBackToNeutral()
    {
        var store = Store();
        store.Save(new Branding { ProductName = "Contoso IT Console" });
        store.Load().IsDefault.Should().BeFalse();

        store.Reset();

        store.Load().IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Save_WritesEveryFieldAsANameSomeoneCouldEdit()
    {
        Store().Save(new Branding { ProductName = "Contoso IT Console" });

        var json = File.ReadAllText(_path);

        json.Should().Contain("productName").And.Contain("chromeColor").And.Contain("accentColor");
        json.Should().NotContain("isDefault", "it is derived, and writing it invites someone to flip it");
    }
}
