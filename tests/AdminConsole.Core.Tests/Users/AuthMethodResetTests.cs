using FluentAssertions;
using Xunit;
using AdminConsole.Core.Models;
using AdminConsole.Core.Users;

namespace AdminConsole.Core.Tests.Users;

/// <summary>Reset MFA and the offboarding step: what goes, in an order Entra accepts, and what stays.</summary>
public sealed class AuthMethodResetTests
{
    private static AuthMethod M(string id, AuthMethodKind kind, string? detail = null, int day = 1) => new()
    {
        Id = id,
        Kind = kind,
        ODataType = "#microsoft.graph.x",
        DisplayName = id,
        Detail = detail,
        CreatedDateTime = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Raw = "{}",
    };

    [Fact]
    public void RemovalOrder_TakesTheMobileNumberLast_AfterTheAlternate()
    {
        // Microsoft: a user cannot keep an alternate mobile without a mobile, so the alternate has to go first.
        var methods = new[]
        {
            M("mobile", AuthMethodKind.Phone, "mobile"),
            M("authenticator", AuthMethodKind.MicrosoftAuthenticator),
            M("alternate", AuthMethodKind.Phone, "alternateMobile"),
            M("office", AuthMethodKind.Phone, "office"),
            M("tap", AuthMethodKind.TemporaryAccessPass),
            M("key", AuthMethodKind.Fido2),
        };

        AuthMethodReset.RemovalOrder(methods).Select(m => m.Id).Should().Equal("tap", "key", "authenticator", "office", "alternate", "mobile");
    }

    [Fact]
    public void RemovalOrder_LeavesOutWhatGraphCannotDelete_AndKeepsTheOldestFirstWithinAKind()
    {
        var methods = new[]
        {
            M("password", AuthMethodKind.Password),
            M("newer", AuthMethodKind.MicrosoftAuthenticator, day: 9),
            M("older", AuthMethodKind.MicrosoftAuthenticator, day: 2),
            M("unknown", AuthMethodKind.Unknown),
        };

        AuthMethodReset.RemovalOrder(methods).Select(m => m.Id).Should().Equal("older", "newer");
    }

    [Fact]
    public void Kept_SaysWhyThePasswordStays()
    {
        var kept = AuthMethodReset.Kept([M("password", AuthMethodKind.Password), M("phone", AuthMethodKind.Phone, "mobile")]);

        kept.Should().ContainSingle();
        kept[0].Method.Id.Should().Be("password");
        kept[0].Reason.Should().Be("The password stays; Reset password changes it.");
    }

    [Fact]
    public void ANumberWithNoTypeIsTreatedAsTheMobile_SoItStillGoesLast()
    {
        AuthMethodReset.RemovalOrder([M("untyped", AuthMethodKind.Phone), M("alternate", AuthMethodKind.Phone, "alternateMobile")])
            .Select(m => m.Id).Should().Equal("alternate", "untyped");
    }
}
