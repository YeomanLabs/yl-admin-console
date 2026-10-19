using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>Connect as another account: what may be typed, how it binds, and asking for the password without a network.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DirectorySignInTests
{
    [Theory]
    [InlineData(@"CORP\admin", null)]
    [InlineData("admin@corp.example.com", null)]
    [InlineData("", "Type the account to connect as, like CORP\\admin or admin@corp.example.com.")]
    [InlineData("admin", "Include the domain: CORP\\admin or admin@corp.example.com.")]
    [InlineData(@"CORP\ad min", "That account is not in a form AD accepts. Use CORP\\admin or admin@corp.example.com.")]
    [InlineData("admin@corp", "That account is not in a form AD accepts. Use CORP\\admin or admin@corp.example.com.")]
    public void Account_MustNameItsDomain(string account, string? expected)
    {
        DirectoryAccount.Problem(account).Should().Be(expected);
    }

    [Fact]
    public void ADownLevelName_SplitsIntoUserAndDomain_AndAUpnStaysWhole()
    {
        var down = DirectoryAccount.ToCredential(@" CORP\admin ", "pw");
        down.UserName.Should().Be("admin");
        down.Domain.Should().Be("CORP");

        var upn = DirectoryAccount.ToCredential("admin@corp.example.com", "pw");
        upn.UserName.Should().Be("admin@corp.example.com");
        upn.Domain.Should().BeEmpty();
    }

    [Fact]
    public void AChosenAccount_ReplacesTheLabCredential()
    {
        var lab = new LdapOptions { Domain = "adlab.test", Credential = new NetworkCredential("lab", "x", "ADLAB") };

        var options = new TenantDomain { Domain = "adlab.test", SignInAs = @"ADLAB\admin" }.ToOptions(lab);

        options.SignInAs.Should().Be(@"ADLAB\admin");
        options.Credential.Should().BeNull("the account the admin chose is asked for, not the lab's");
    }

    [Fact]
    public async Task WithNoPassword_TheStatusSaysNotSignedIn_WithoutTouchingTheNetwork()
    {
        var credentials = new FakeCredentials(answer: null);
        using var service = new LdapDirectoryService(new LdapOptions { Domain = "corp.example.com", SignInAs = @"CORP\admin", Credentials = credentials }, NullLogger<LdapDirectoryService>.Instance);

        var status = await service.GetStatusAsync(CancellationToken.None);

        status.IsConnected.Should().BeFalse();
        status.NeedsSignIn.Should().BeTrue();
        status.Reason.Should().Be(@"Not signed in to corp.example.com. Sign in as CORP\admin to use on-prem features.");
        credentials.Asked.Should().Equal((@"CORP\admin", false));
    }

    [Fact]
    public async Task SignInAgain_AsksEvenAfterADecline()
    {
        var credentials = new FakeCredentials(answer: null);
        using var service = new LdapDirectoryService(new LdapOptions { Domain = "corp.example.com", SignInAs = @"CORP\admin", Credentials = credentials }, NullLogger<LdapDirectoryService>.Instance);

        await service.GetStatusAsync(CancellationToken.None);
        service.Reconnect();
        await service.GetStatusAsync(CancellationToken.None);
        await service.GetStatusAsync(CancellationToken.None);

        credentials.Asked.Select(a => a.AskAgain).Should().Equal(false, true, false);
    }

    private sealed class FakeCredentials(NetworkCredential? answer) : IDirectoryCredentials
    {
        public List<(string Account, bool AskAgain)> Asked { get; } = [];

        public Task<NetworkCredential?> GetAsync(string domain, string userName, bool askAgain, CancellationToken ct)
        {
            Asked.Add((userName, askAgain));
            return Task.FromResult(answer);
        }

        public void Forget(string domain, string userName)
        {
        }
    }
}
