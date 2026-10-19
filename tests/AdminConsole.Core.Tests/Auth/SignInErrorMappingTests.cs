using FluentAssertions;
using Microsoft.Identity.Client;
using Xunit;
using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Tests.Auth;

/// <summary>
/// What an admin reads when the broker refuses: Entra's own reason, not "WAM_provider_error_" and a number. Found when
/// "Grant all write permissions" in a work tenant said only "Sign-in failed (WAM_provider_error_3399614468)".
/// </summary>
public sealed class SignInErrorMappingTests
{
    private const string Wam = "WAM_provider_error_3399614468";
    private const string Link = "https://login.microsoftonline.com/t-1/adminconsent?client_id=c-1";

    private static string BrokerText(string entra) =>
        $"WAM Error \r\n Error Code: 3399614468 \r\n Error Message: {entra} Trace ID: 0a1b Correlation ID: 2c3d Timestamp: 2026-10-19 13:30:00Z \r\n Internal Error Code: 557973645";

    [Theory]
    [InlineData("AADSTS90094: The grant requires admin permission.")]
    [InlineData("AADSTS65001: The user or administrator has not consented to use the application with ID 'c-1' named 'Admin Console'.")]
    [InlineData("AADSTS90095: An admin consent request may be required.")]
    public void ConsentOnlyAnAdminCanGive_SaysWhoAndHow_WithEntrasReason(string entra)
    {
        var mapped = AuthService.Map(new MsalServiceException(Wam, BrokerText(entra)), Link);

        mapped.Kind.Should().Be(SignInFailureKind.AdminConsentRequired);
        mapped.Message.Should().StartWith("An admin has to approve these permissions in this tenant.")
            .And.Contain(Link)
            .And.EndWith($"Entra said: {entra}");
        mapped.Message.Should().NotContain("Trace ID", "the ids that follow Entra's sentence are noise to the reader");
        mapped.ErrorCode.Should().Be(Wam);
        mapped.RawMessage.Should().Contain("Correlation ID: 2c3d", "the details expander keeps everything");
    }

    [Fact]
    public void WithoutAKnownTenant_ItNamesThePortalRoute_NotABrokenLink()
    {
        var mapped = AuthService.Map(new MsalServiceException(Wam, BrokerText("AADSTS90094: The grant requires admin permission.")));

        mapped.Message.Should().Contain("Enterprise applications → this app → Permissions → Grant admin consent").And.NotContain("adminconsent?");
    }

    [Fact]
    public void ARegistrationThatDoesNotListTheApi_PointsAtTheScript()
    {
        var mapped = AuthService.Map(new MsalServiceException(Wam,
            BrokerText("AADSTS650057: Invalid resource. The client has requested access to a resource which isn't listed in the requested permissions in the client's application registration.")), Link);

        mapped.Kind.Should().Be(SignInFailureKind.AdminConsentRequired);
        mapped.Message.Should().Contain("New-AppRegistration.ps1 -Update").And.NotContain(Link);
    }

    [Fact]
    public void AnythingElse_ShowsEntrasSentence_RatherThanTheBrokerNumber()
    {
        var mapped = AuthService.Map(new MsalServiceException(Wam, BrokerText("AADSTS50020: User account from identity provider does not exist in tenant.")), Link);

        mapped.Kind.Should().Be(SignInFailureKind.Other);
        mapped.Message.Should().Be("Sign-in failed: AADSTS50020: User account from identity provider does not exist in tenant.");
    }

    [Fact]
    public void WithNoEntraReasonAtAll_TheCodeIsStillSaid()
    {
        var mapped = AuthService.Map(new MsalClientException(Wam, "The broker is unavailable."));

        mapped.Message.Should().Be($"Sign-in failed ({Wam}).");
    }

    [Fact]
    public void ADeclinedPrompt_StaysDeclined()
    {
        AuthService.Map(new MsalServiceException("access_denied", "AADSTS65004: User declined to consent to access the app.")).Kind
            .Should().Be(SignInFailureKind.ConsentDeclined);
    }

    [Theory]
    [InlineData("x AADSTS90094: y", "AADSTS90094")]
    [InlineData("no code here", null)]
    [InlineData(null, null)]
    public void EntraCode_FindsTheCodeWhereverItSits(string? text, string? expected)
    {
        AuthService.EntraCode(text).Should().Be(expected);
    }
}
