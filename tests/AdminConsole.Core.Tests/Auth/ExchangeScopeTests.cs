using FluentAssertions;
using Xunit;
using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Tests.Auth;

/// <summary>M13.3 — Exchange is a second resource, so its scope is named, granted and checked differently from Graph's.</summary>
public sealed class ExchangeScopeTests
{
    [Theory]
    [InlineData("https://outlook.office365.com/Exchange.Manage", "https://outlook.office365.com/", "Exchange.Manage")]
    [InlineData("User.Read.All", "", "User.Read.All")]
    [InlineData("openid", "", "openid")]
    public void ResourceAndShortName(string scope, string resource, string shortName)
    {
        Scopes.ResourceOf(scope).Should().Be(resource);
        Scopes.ShortName(scope).Should().Be(shortName);
    }

    [Fact]
    public void TheExchangeScopeIsInNoGraphTier()
    {
        // Mixing resources in one interactive request is refused by Entra, so Exchange must never ride along with the
        // Graph write tier's one-shot consent.
        Scopes.Read.Concat(Scopes.Write).Concat(Scopes.Sensitive).Should().NotContain(Scopes.ExchangeManage);
        Scopes.Exchange.Should().Equal(Scopes.ExchangeManage);
    }

    [Fact]
    public void MailboxesAreACapability_ListedOnThePermissionsPage()
    {
        Capabilities.All.Should().Contain(Capabilities.ManageMailboxes);
        Capabilities.ManageMailboxes.Scopes.Should().Equal(Scopes.ExchangeManage);
        Capabilities.ManageMailboxes.RoleHint.Should().Contain("Exchange Recipient Administrator");
    }

    [Fact]
    public void WithoutTheExchangeGrant_TheReasonNamesThePermissionAsTheRegistrationDoes()
    {
        var tenant = new TenantContext();
        tenant.Update(new TenantContextSnapshot { TenantId = "t", GrantedScopes = [.. Scopes.Read, .. Scopes.Write] });

        var result = new PermissionChecker(tenant).Check(Capabilities.ManageMailboxes);

        result.NeedsConsent.Should().BeTrue();
        result.Reason.Should().Be("Needs the 'Exchange.Manage' permission — click to grant");
    }

    [Fact]
    public void WithTheExchangeGrant_AnExchangeAdministratorIsAllowed()
    {
        var tenant = new TenantContext();
        tenant.Update(new TenantContextSnapshot
        {
            TenantId = "t",
            GrantedScopes = [.. Scopes.Read, Scopes.ExchangeManage],
            Roles = [DirectoryRoleRef.From(null, DirectoryRoleRef.ExchangeRecipientAdministrator)],
        });

        new PermissionChecker(tenant).Check(Capabilities.ManageMailboxes).State.Should().Be(PermissionState.Allowed);
    }

    [Fact]
    public void AGraphScopeOfTheSameShortName_DoesNotCountAsTheExchangeGrant()
    {
        // An Exchange token's scp also lists names like User.Read.All. Granted Exchange scopes are recorded with their
        // resource prefix so they can never satisfy, or be satisfied by, a Graph scope.
        var tenant = new TenantContext();
        tenant.Update(new TenantContextSnapshot { TenantId = "t", GrantedScopes = ["Exchange.Manage"] });

        tenant.HasScope(Scopes.ExchangeManage).Should().BeFalse();
    }
}
