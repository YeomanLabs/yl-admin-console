using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Tests.Auth;

/// <summary>M10.1: which registration this installation signs in with, kept where a client can actually set it.</summary>
public sealed class TenantConfigStoreTests : IDisposable
{
    private const string Shipped = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yl-tenant-{Guid.NewGuid():N}");
    private readonly string _path;

    public TenantConfigStoreTests() => _path = Path.Combine(_dir, "tenant.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TenantConfigStore Store(string shippedClientId = Shipped) =>
        new(_path, new AuthOptions { ClientId = shippedClientId }, NullLogger<TenantConfigStore>.Instance);

    [Fact]
    public void Load_WithNothingSaved_FallsBackToTheShippedRegistration()
    {
        var config = Store().Load();

        config.ClientId.Should().Be(Shipped);
        config.IsConfigured.Should().BeTrue();
        config.Authority.Should().Be("https://login.microsoftonline.com/organizations",
            "the default authority works for any tenant");
    }

    [Fact]
    public void Load_WithNothingSavedAndNothingShipped_IsUnconfigured_SoTheAppCanAskForIt()
    {
        // This is the state a client installation starts in, and it must be a setup screen rather than a failure
        // inside MSAL that nobody can act on.
        Store(shippedClientId: string.Empty).Load().IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void Save_ThenLoad_PrefersTheInstallationsOwnRegistration()
    {
        var store = Store();
        store.Save(new TenantConfig { ClientId = Client, TenantHint = "contoso.onmicrosoft.com" });

        var config = store.Load();

        config.ClientId.Should().Be(Client, "what the client configured beats what the build shipped with");
        config.TenantHint.Should().Be("contoso.onmicrosoft.com");
    }

    [Fact]
    public void Save_TrimsTheClientId_BecauseItArrivesPasted()
    {
        var store = Store();
        store.Save(new TenantConfig { ClientId = $"  {Client}\r\n" });

        store.Load().ClientId.Should().Be(Client);
    }

    [Fact]
    public void Save_DoesNotWriteTheDerivedFlag_BecauseTheFileIsHandEditable()
    {
        Store().Save(new TenantConfig { ClientId = Client });

        var json = File.ReadAllText(_path);

        json.Should().NotContain("isConfigured",
            "it is computed from the client ID, and writing it invites someone to flip it and expect that to mean something");
        json.Should().Contain("clientId").And.Contain("authority");
    }

    [Fact]
    public void Clear_ForgetsTheInstallationsRegistration()
    {
        var store = Store(shippedClientId: string.Empty);
        store.Save(new TenantConfig { ClientId = Client });
        store.Load().ClientId.Should().Be(Client);

        store.Clear();

        store.Load().IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void Load_ACorruptFile_FallsBackRatherThanFailingToStart()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "{ not json");

        Store().Load().ClientId.Should().Be(Shipped);
    }

    [Fact]
    public void Load_AFileWithAMeaninglessClientId_IsIgnored()
    {
        // A half-finished edit should not strand the app on a value that can never sign in.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, """{"clientId":"paste-it-here","authority":"https://login.microsoftonline.com/organizations"}""");

        Store().Load().ClientId.Should().Be(Shipped);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-a-guid", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("22222222-2222-2222-2222-222222222222", true)]
    [InlineData("  22222222-2222-2222-2222-222222222222  ", true)]
    public void LooksLikeClientId_CatchesWhatSomeoneWouldActuallyPaste(string? value, bool expected)
    {
        TenantConfig.LooksLikeClientId(value).Should().Be(expected);
    }
}
