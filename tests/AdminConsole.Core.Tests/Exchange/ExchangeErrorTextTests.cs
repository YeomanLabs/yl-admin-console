using FluentAssertions;
using Xunit;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Exchange;

public sealed class ExchangeErrorTextTests
{
    private static ExchangeException From(int status, string fixture) =>
        ExchangeException.FromResponse(status, Fixtures.Text(fixture), null, "Get-X", DateTimeOffset.UtcNow);

    [Fact]
    public void NotFound_SaysWhatItProbablyMeans()
    {
        ExchangeErrorText.Friendly(From(404, "exchange/error-not-found"))
            .Should().StartWith("Exchange can't find that mailbox or recipient");
    }

    [Fact]
    public void BadParameter_IsOwnedAsAConsoleBug()
    {
        ExchangeErrorText.Friendly(From(400, "exchange/error-bad-parameter"))
            .Should().Contain("bug in the console");
    }

    [Fact]
    public void Forbidden_NamesTheRolesThatCan()
    {
        var ex = new ExchangeException(403, "Forbidden", "Access is denied.");

        ExchangeErrorText.Friendly(ex).Should().Contain("Exchange Recipient Administrator");
    }

    [Fact]
    public void WriteScope_IsAPermissionProblem_NotANotFound()
    {
        var ex = new ExchangeException(400, "BadRequest",
            "The operation couldn't be performed because 'x' is outside the scope; it isn't within your current write scope.",
            "Microsoft.Exchange.Configuration.Tasks.TaskInvalidOperationException");

        ExchangeErrorText.Friendly(ex).Should().StartWith("Your account doesn't have an Exchange role");
    }

    [Fact]
    public void SyncedObject_SendsTheAdminToAd()
    {
        var ex = new ExchangeException(400, "BadRequest",
            "An Active Directory object is being synchronized from your on-premises organization. This action should be performed there.",
            "Microsoft.Exchange.Configuration.Tasks.TaskInvalidOperationException");

        ExchangeErrorText.Friendly(ex).Should().Contain("synced from on-prem AD");
    }

    [Fact]
    public void Throttled_SaysHowLong()
    {
        var ex = new ExchangeException(429, "TooManyRequests", "slow", retryAfter: TimeSpan.FromSeconds(12.2));

        ExchangeErrorText.Friendly(ex).Should().Contain("13 s");
    }

    [Fact]
    public void Unknown_FallsBackToExchangesOwnText()
    {
        var ex = new ExchangeException(400, "BadRequest", "Something specific Exchange said.", "Microsoft.Exchange.SomethingElseException");

        ExchangeErrorText.Friendly(ex).Should().Be("Something specific Exchange said.");
    }
}
