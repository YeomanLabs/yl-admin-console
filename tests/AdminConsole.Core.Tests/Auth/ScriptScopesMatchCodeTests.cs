using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Tests.Auth;

/// <summary>
/// M10.4 — the registration script asks Entra for a set of permissions, and the app asks MSAL for a set of scopes. If
/// those two lists drift, a tenant set up by the script gets a console that fails at the first write with a consent
/// error, in that customer's tenant, long after anyone would connect it to the edit that caused it.
/// </summary>
/// <remarks>
/// The alternative — generating one from the other — would mean the script fetching a list at runtime from something
/// it cannot reach, or a build step emitting PowerShell. Comparing them is duller and catches the same mistake.
/// </remarks>
public sealed class ScriptScopesMatchCodeTests
{
    private static readonly string ScriptPath = FindScript();

    [Fact]
    public void TheScriptRequestsExactlyTheScopesTheAppSignsInWith()
    {
        var script = File.ReadAllText(ScriptPath);

        Array("ReadScopes", script).Should().BeEquivalentTo(Scopes.Read, "the read tier is consented at first sign-in");
        Array("WriteScopes", script).Should().BeEquivalentTo(Scopes.Write, "the write tier is asked for on the first write");
        Array("SensitiveScopes", script).Should().BeEquivalentTo(Scopes.Sensitive, "the sensitive tier is asked for at a reveal");
        Array("FilesScopes", script).Should().BeEquivalentTo(Scopes.Files, "the files tier is asked for at the first export");

        // The script names Exchange's permission as the registration does; the app asks MSAL for it by resource URI.
        Array("ExchangeScopes", script).Should().BeEquivalentTo(Scopes.Exchange.Select(Scopes.ShortName),
            "Exchange Online is its own resource with its own consent");
        Array("SharePointScopes", script).Should().BeEquivalentTo(Scopes.SharePoint.Select(Scopes.ShortName),
            "SharePoint is its own resource too, asked for by its application id");
    }

    [Fact]
    public void TheScriptWritesConfigWhereTheConsoleReadsIt()
    {
        // M11 moved %LOCALAPPDATA%\YeomanLabs\AdminConsole to %LOCALAPPDATA%\AdminConsole and the script kept writing to
        // the old one, so -WriteConfig produced a tenant.json the console never read.
        var script = File.ReadAllText(ScriptPath);
        var expected = Path.GetFileName(AdminConsole.Core.AppPaths.Root);

        script.Should().Contain($"Join-Path $env:LOCALAPPDATA '{expected}'");
        script.Should().NotContain(@"'YeomanLabs\AdminConsole'");
    }

    [Fact]
    public void TheScriptNeverAsksForAnApplicationPermission()
    {
        // The console signs in as the admin and holds no secret. An application permission in the registration would
        // mean it could act with nobody signed in, which CLAUDE.md forbids outright.
        var script = File.ReadAllText(ScriptPath);

        // Precisely the resourceAccess type, not the word: the script's own help mentions "Privileged Role
        // Administrator" as a role the operator needs, which is a different thing entirely.
        Regex.Matches(script, @"type\s*=\s*'(?<type>\w+)'").Select(m => m.Groups["type"].Value)
            .Should().OnlyContain(t => t == "Scope", "a requested permission must be delegated, never application-only");
        script.Should().NotContain("Directory.ReadWrite.All");
        script.Should().NotContain("Directory.AccessAsUser.All");
        script.Should().NotContain("clientSecret");
        script.Should().NotContain("addPassword");
    }

    [Fact]
    public void TheScriptSupportsBeingRunHarmlesslyFirst()
    {
        var script = File.ReadAllText(ScriptPath);

        script.Should().Contain("SupportsShouldProcess", "the script must accept -WhatIf");
        script.Should().Contain("WhatIfPreference", "and must actually branch on it before creating anything");
    }

    /// <summary>Reads a <c>$Name = @( 'a' 'b' )</c> literal out of the script.</summary>
    private static string[] Array(string variable, string script)
    {
        var block = Regex.Match(script, @"\$" + variable + @"\s*=\s*@\((?<body>[^)]*)\)", RegexOptions.Singleline);
        block.Success.Should().BeTrue($"the script should define ${variable}");

        return [.. Regex.Matches(block.Groups["body"].Value, @"'(?<scope>[^']+)'").Select(m => m.Groups["scope"].Value)];
    }

    private static string FindScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "New-AppRegistration.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("scripts/New-AppRegistration.ps1 was not found above the test output directory.");
    }
}
