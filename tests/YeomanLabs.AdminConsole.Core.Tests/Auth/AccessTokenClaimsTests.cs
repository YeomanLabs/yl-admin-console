using FluentAssertions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.Core.Tests.Auth;

public sealed class AccessTokenClaimsTests
{
    [Fact]
    public void GetScopes_ReadsSpaceSeparatedScpClaim()
    {
        var token = AccessTokenClaims.CreateUnsignedToken(new
        {
            scp = "User.Read User.Read.All  BitlockerKey.Read.All",
            tid = "c570b99a-c52d-47f4-997e-59c3b3205cf6",
        });

        AccessTokenClaims.GetScopes(token).Should().Equal("User.Read", "User.Read.All", "BitlockerKey.Read.All");
        AccessTokenClaims.GetTenantId(token).Should().Be("c570b99a-c52d-47f4-997e-59c3b3205cf6");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.!!!.c")]
    public void GetScopes_ReturnsEmptyForGarbage(string token)
    {
        AccessTokenClaims.GetScopes(token).Should().BeEmpty();
        AccessTokenClaims.GetTenantId(token).Should().BeNull();
    }

    [Fact]
    public void GetScopes_ReturnsEmptyWhenScpMissing()
    {
        var token = AccessTokenClaims.CreateUnsignedToken(new { aud = "graph" });
        AccessTokenClaims.GetScopes(token).Should().BeEmpty();
    }

    [Fact]
    public void ScopeMatching_IsCaseInsensitive()
    {
        Scopes.Matches("BitlockerKey.Read.All", "BitLockerKey.Read.All").Should().BeTrue();
        Scopes.Read.Should().HaveCount(12);
        Scopes.Write.Should().HaveCount(8);
        Scopes.Sensitive.Should().HaveCount(2);
    }
}
