using FluentAssertions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Models;

public sealed class AuthMethodTests
{
    [Theory]
    [InlineData("#microsoft.graph.passwordAuthenticationMethod", AuthMethodKind.Password, null)]
    [InlineData("#microsoft.graph.microsoftAuthenticatorAuthenticationMethod", AuthMethodKind.MicrosoftAuthenticator, "microsoftAuthenticatorMethods")]
    [InlineData("#microsoft.graph.phoneAuthenticationMethod", AuthMethodKind.Phone, "phoneMethods")]
    [InlineData("#microsoft.graph.fido2AuthenticationMethod", AuthMethodKind.Fido2, "fido2Methods")]
    [InlineData("#microsoft.graph.windowsHelloForBusinessAuthenticationMethod", AuthMethodKind.WindowsHelloForBusiness, "windowsHelloForBusinessMethods")]
    [InlineData("#microsoft.graph.emailAuthenticationMethod", AuthMethodKind.Email, "emailMethods")]
    [InlineData("#microsoft.graph.softwareOathAuthenticationMethod", AuthMethodKind.SoftwareOath, "softwareOathMethods")]
    [InlineData("#microsoft.graph.temporaryAccessPassAuthenticationMethod", AuthMethodKind.TemporaryAccessPass, "temporaryAccessPassMethods")]
    [InlineData("#microsoft.graph.somethingNewAuthenticationMethod", AuthMethodKind.Unknown, null)]
    public void KindAndDeleteCollection_FollowDoc04(string odataType, AuthMethodKind kind, string? collection)
    {
        var m = AuthMethod.FromJson(Fixtures.Parse($$"""{"@odata.type":"{{odataType}}","id":"m1"}"""));

        m.Kind.Should().Be(kind);
        m.DeleteCollectionName.Should().Be(collection);
        m.CanDelete.Should().Be(collection is not null);
    }

    [Fact]
    public void DisplayName_UsesTheKindSpecificField()
    {
        var phone = AuthMethod.FromJson(Fixtures.Parse("""{"@odata.type":"#microsoft.graph.phoneAuthenticationMethod","id":"p","phoneNumber":"+1 4255550100","phoneType":"mobile"}"""));
        var authenticator = AuthMethod.FromJson(Fixtures.Parse("""{"@odata.type":"#microsoft.graph.microsoftAuthenticatorAuthenticationMethod","id":"a","displayName":"Pixel 9","deviceTag":"SoftwareTokenActivated","createdDateTime":"2026-05-01T00:00:00Z"}"""));
        var fido = AuthMethod.FromJson(Fixtures.Parse("""{"@odata.type":"#microsoft.graph.fido2AuthenticationMethod","id":"f","displayName":"YubiKey 5","model":"YubiKey 5 NFC"}"""));
        var email = AuthMethod.FromJson(Fixtures.Parse("""{"@odata.type":"#microsoft.graph.emailAuthenticationMethod","id":"e","emailAddress":"x@example.com"}"""));
        var tap = AuthMethod.FromJson(Fixtures.Parse("""{"@odata.type":"#microsoft.graph.temporaryAccessPassAuthenticationMethod","id":"t","isUsable":true}"""));

        phone.DisplayName.Should().Be("+1 4255550100");
        phone.Detail.Should().Be("mobile");
        authenticator.DisplayName.Should().Be("Pixel 9");
        authenticator.CreatedDateTime.Should().NotBeNull();
        fido.Detail.Should().Be("YubiKey 5 NFC");
        fido.KindLabel.Should().Be("Passkey (FIDO2)");
        email.DisplayName.Should().Be("x@example.com");
        tap.Detail.Should().Be("usable");
    }
}
