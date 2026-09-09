using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Auth;

public sealed class PermissionCheckerTests
{
    private static TenantContext Context(IEnumerable<string> scopes, params string[] roles)
    {
        var ctx = new TenantContext();
        ctx.Update(new TenantContextSnapshot
        {
            TenantId = "t",
            GrantedScopes = scopes.ToList(),
            Roles = roles.Select(r => DirectoryRoleRef.From(null, r)).ToList(),
        });
        return ctx;
    }

    [Fact]
    public void MissingScope_NeedsConsent_WithTheUxSection6Caption()
    {
        var checker = new PermissionChecker(Context(Scopes.Read));

        var r = checker.Check(Capabilities.EditUser);

        r.State.Should().Be(PermissionState.NeedsConsent);
        r.CanRun.Should().BeFalse();
        r.MissingScopes.Should().Equal(Scopes.UserReadWriteAll);
        r.Reason.Should().Be("Needs the 'User.ReadWrite.All' permission — click to grant");
    }

    [Fact]
    public void ScopeAndRole_IsAllowed_WithNoCaption()
    {
        var checker = new PermissionChecker(Context([.. Scopes.Read, Scopes.UserReadWriteAll], DirectoryRoleRef.UserAdministrator));

        var r = checker.Check(Capabilities.EditUser);

        r.State.Should().Be(PermissionState.Allowed);
        r.CanRun.Should().BeTrue();
        r.Reason.Should().BeNull();
    }

    [Fact]
    public void GlobalAdmin_SatisfiesEveryRoleRequirement()
    {
        var all = Scopes.Read.Concat(Scopes.Write).Concat(Scopes.Sensitive).ToList();
        var checker = new PermissionChecker(Context(all, DirectoryRoleRef.GlobalAdministrator));

        Capabilities.All.Should().OnlyContain(c => checker.Check(c).State == PermissionState.Allowed);
    }

    [Fact]
    public void ScopeButNoRole_IsAllowedWithAWarning_NotBlocked()
    {
        var checker = new PermissionChecker(Context([.. Scopes.Read, Scopes.UserReadWriteAll], DirectoryRoleRef.ReportsReader));

        var r = checker.Check(Capabilities.EditUser);

        r.State.Should().Be(PermissionState.AllowedWithRoleWarning);
        r.CanRun.Should().BeTrue("scopes are necessary, not sufficient — let Graph decide");
        r.Reason.Should().Be("Your account may lack a role that allows this (User Administrator)");
    }

    [Fact]
    public void ScopeMatching_IsCaseInsensitive_SoTheServiceCasingOfBitlockerWorks()
    {
        var checker = new PermissionChecker(Context([.. Scopes.Read, "bitlockerkey.read.all"], DirectoryRoleRef.CloudDeviceAdministrator));

        checker.Check(Capabilities.RevealBitLocker).State.Should().Be(PermissionState.Allowed);
    }

    [Fact]
    public void ForbiddenReason_MatchesTheUxSection6Wording()
    {
        PermissionChecker.ForbiddenReason(Capabilities.ResetPassword)
            .Should().Be("Your account lacks a role that allows this (User Administrator or Helpdesk Administrator).");
    }

    [Fact]
    public void Catalog_CoversEveryWriteAndSensitiveScope()
    {
        var covered = Capabilities.All.SelectMany(c => c.Scopes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Scopes.Write.Should().OnlyContain(s => covered.Contains(s, StringComparer.OrdinalIgnoreCase));
        Scopes.Sensitive.Should().OnlyContain(s => covered.Contains(s, StringComparer.OrdinalIgnoreCase));
        Capabilities.All.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        Capabilities.All.Where(c => c.IsSensitive).Should().HaveCount(2);
    }

    [Fact]
    public async Task Consent_PromptsOnce_ThenUpdatesTheTenantContext()
    {
        var auth = new FakeAuthService();
        var tenant = Context(Scopes.Read);
        var checker = new PermissionChecker(tenant);
        var sut = new ConsentService(auth, tenant, checker, NullLogger<ConsentService>.Instance);

        var before = checker.Check(Capabilities.EditUser);
        before.NeedsConsent.Should().BeTrue();

        var after = await sut.EnsureAsync(Capabilities.EditUser, CancellationToken.None);

        after.CanRun.Should().BeTrue();
        auth.ConsentPrompts.Should().Be(1);
        tenant.HasScope(Scopes.UserReadWriteAll).Should().BeTrue("the context is refreshed so every grey rule re-evaluates");

        await sut.EnsureAsync(Capabilities.EditUser, CancellationToken.None);
        auth.ConsentPrompts.Should().Be(1, "already consented — no second prompt");
    }

    [Fact]
    public async Task Consent_Declined_LeavesTheStateUnchanged()
    {
        var auth = new FakeAuthService { ConsentSucceeds = false };
        var tenant = Context(Scopes.Read);
        var sut = new ConsentService(auth, tenant, new PermissionChecker(tenant), NullLogger<ConsentService>.Instance);

        var act = () => sut.EnsureAsync(Capabilities.EditUser, CancellationToken.None);

        (await act.Should().ThrowAsync<SignInException>()).Which.Kind.Should().Be(SignInFailureKind.ConsentDeclined);
        tenant.HasScope(Scopes.UserReadWriteAll).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureWriteScopes_GrantsTheWholeWriteTierInOnePrompt_ButNotSensitive()
    {
        var auth = new FakeAuthService();
        var tenant = Context(Scopes.Read);
        var sut = new ConsentService(auth, tenant, new PermissionChecker(tenant), NullLogger<ConsentService>.Instance);

        await sut.EnsureWriteScopesAsync(CancellationToken.None);

        auth.ConsentPrompts.Should().Be(1);
        Scopes.Write.Should().OnlyContain(s => tenant.HasScope(s));
        Scopes.Sensitive.Should().OnlyContain(s => !tenant.HasScope(s), "sensitive scopes are requested only at the reveal");
    }
}
