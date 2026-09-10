using FluentAssertions;
using Xunit;
using AdminConsole.Core;

namespace AdminConsole.Core.Tests;

/// <summary>
/// M11.1 — the rename moved where everything is kept. These pin the layout and the migration, since getting the
/// migration wrong looks to an admin exactly like a factory reset.
/// </summary>
/// <remarks>
/// Every test here works inside a temporary directory. The first version of this file called
/// <c>AppPaths.MigrateLegacyRoot()</c> directly, which is not a pure function — running the suite moved the real
/// profile's cache, templates, action log and token cache on the machine doing the testing, and destroyed them. A
/// unit test must never be able to reach a real installation.
/// </remarks>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yl-paths-{Guid.NewGuid():N}");

    private string From => Path.Combine(_dir, "old", "AdminConsole");
    private string To => Path.Combine(_dir, "new", "AdminConsole");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---- the layout ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheDataFolderCarriesNoCompanyName()
    {
        // The point of M11: a client's console should not keep its files under somebody else's name.
        AppPaths.Root.Should().EndWith(Path.Combine("Local", "AdminConsole"));
        AppPaths.Root.Should().NotContain("YeomanLabs");
    }

    [Fact]
    public void EverythingLivesUnderTheOneRoot()
    {
        foreach (var path in new[] { AppPaths.CacheDirectory, AppPaths.TemplatesDirectory, AppPaths.LogsDirectory, AppPaths.ActionsDbPath })
        {
            path.Should().StartWith(AppPaths.Root);
        }

        AppPaths.CacheDbPath("tenant-1").Should().Be(Path.Combine(AppPaths.CacheDirectory, "tenant-1.db"));
    }

    [Fact]
    public void TheLegacyFolderIsStillNamed_SoAnOldInstallCanBeFound()
    {
        AppPaths.LegacyRoot.Should().EndWith(Path.Combine("YeomanLabs", "AdminConsole"));
        AppPaths.LegacyRoot.Should().NotBe(AppPaths.Root);
    }

    // ---- the migration ------------------------------------------------------------------------------------------

    [Fact]
    public void Migrate_CarriesEverythingAcross()
    {
        SeedInstallation(From);

        AppPaths.Migrate(From, To).Should().BeTrue();

        Directory.Exists(From).Should().BeFalse("a move, so there is only ever one copy");
        File.ReadAllText(Path.Combine(To, "actions.db")).Should().Be("the write history");
        File.ReadAllText(Path.Combine(To, "msal.cache")).Should().Be("the token cache");
        File.ReadAllText(Path.Combine(To, "templates", "starter.json")).Should().Be("a saved template");
        File.ReadAllText(Path.Combine(To, "cache", "tenant-1.db")).Should().Be("the sync cache");
    }

    [Fact]
    public void Migrate_WithNothingToMove_DoesNothing()
    {
        // The overwhelmingly common case: a fresh install. It must be silent and must not create either folder.
        AppPaths.Migrate(From, To).Should().BeFalse();
        Directory.Exists(To).Should().BeFalse("asking must not bring the destination into existence");
    }

    [Fact]
    public void Migrate_WhenItHasAlreadyRun_LeavesBothAlone()
    {
        SeedInstallation(From);
        Directory.CreateDirectory(To);
        File.WriteAllText(Path.Combine(To, "actions.db"), "the newer history");

        AppPaths.Migrate(From, To).Should().BeFalse("the destination already exists, so this has run before");

        File.ReadAllText(Path.Combine(To, "actions.db")).Should().Be("the newer history", "and must not be overwritten");
        Directory.Exists(From).Should().BeTrue("nor may the older copy be deleted on the strength of a guess");
    }

    [Fact]
    public void Migrate_IsSafeToCallTwice()
    {
        SeedInstallation(From);

        AppPaths.Migrate(From, To).Should().BeTrue();
        AppPaths.Migrate(From, To).Should().BeFalse();

        File.ReadAllText(Path.Combine(To, "actions.db")).Should().Be("the write history");
    }

    private static void SeedInstallation(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        File.WriteAllText(Path.Combine(root, "actions.db"), "the write history");
        File.WriteAllText(Path.Combine(root, "msal.cache"), "the token cache");
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(root, "templates", "starter.json"), "a saved template");
        File.WriteAllText(Path.Combine(root, "cache", "tenant-1.db"), "the sync cache");
    }
}
