using FluentAssertions;
using Xunit;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Exchange;

public sealed class ExchangeValuesTests
{
    [Theory]
    [InlineData("82.63 MB (86,646,374 bytes)", 86_646_374L)]
    [InlineData("100 GB (107,374,182,400 bytes)", 107_374_182_400L)]
    [InlineData("0 B (0 bytes)", 0L)]
    [InlineData("1.327 GB (1,424,828,607 bytes)", 1_424_828_607L)]
    [InlineData("9.659 MB (10.128.522 bytes)", 10_128_522L)]
    [InlineData("12345", 12_345L)]
    public void Size_ReadsTheByteCountInBrackets_NotTheRoundedFigure(string text, long bytes)
    {
        var size = ExchangeSize.Parse(text);

        size.Bytes.Should().Be(bytes);
        size.IsUnlimited.Should().BeFalse();
    }

    [Theory]
    [InlineData("Unlimited")]
    [InlineData("unlimited")]
    public void Size_Unlimited_IsKnownButHasNoBytes(string text)
    {
        var size = ExchangeSize.Parse(text);

        size.IsUnlimited.Should().BeTrue();
        size.IsKnown.Should().BeTrue();
        size.ToDisplay().Should().Be("Unlimited");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a lot")]
    [InlineData("82.63 MB")]
    public void Size_Unparseable_IsUnknown_RatherThanZero(string? text)
    {
        var size = ExchangeSize.Parse(text);

        size.IsKnown.Should().BeFalse();
        size.ToDisplay().Should().Be("Not reported");
    }

    [Fact]
    public void Size_DisplaysInBinaryUnits_LikeExchange()
    {
        ExchangeSize.Parse("82.63 MB (86,646,374 bytes)").ToDisplay().Should().Be((82.63).ToString("0.##") + " MB");
        new ExchangeSize(512, false).ToDisplay().Should().Be("512 bytes");
    }

    [Fact]
    public void Size_FractionOfAQuota()
    {
        var used = ExchangeSize.Parse("82.63 MB (86,646,374 bytes)");

        used.FractionOf(ExchangeSize.Parse("100 GB (107,374,182,400 bytes)")).Should().BeApproximately(0.000807, 0.000001);
        used.FractionOf(ExchangeSize.Unlimited).Should().BeNull();
    }

    [Fact]
    public void Rights_SplitsTheCommaJoinedFullAccessElement()
    {
        var rows = Fixtures.Items("exchange/mailbox-permissions-shared");

        ExchangeValues.Rights(rows[0]).Should().Equal("FullAccess", "ReadPermission");
        ExchangeValues.Rights(rows[1]).Should().Equal("FullAccess", "ExternalAccount", "ReadPermission");
        ExchangeValues.Rights(rows[2]).Should().Equal("FullAccess");
    }

    [Fact]
    public void Rights_SendAsArrivesPlain()
    {
        var rows = Fixtures.Items("exchange/recipient-permissions-shared");

        ExchangeValues.Rights(rows[1]).Should().Equal("SendAs");
    }

    [Fact]
    public void Self_IsRecognisedByNameOrSid()
    {
        var rows = Fixtures.Items("exchange/mailbox-permissions-shared");

        ExchangeValues.IsSelf(ExchangeValues.String(rows[0], "User"), ExchangeValues.String(rows[0], "UserSid")).Should().BeTrue();
        ExchangeValues.IsSelf(ExchangeValues.String(rows[2], "User"), ExchangeValues.String(rows[2], "UserSid")).Should().BeFalse();
        ExchangeValues.IsSelf(null, "S-1-5-10").Should().BeTrue();
    }

    [Fact]
    public void Readers_IgnoreAnnotations_AndTreatEmptyAsNull()
    {
        var mailbox = Fixtures.Item("exchange/mailbox", 0);

        ExchangeValues.String(mailbox, "RecipientTypeDetails").Should().Be("UserMailbox");
        ExchangeValues.Strings(mailbox, "EmailAddresses").Should().Contain("SMTP:Adele@contoso.com");
        ExchangeValues.NonEmpty(mailbox, "LitigationHoldOwner").Should().BeNull();
        ExchangeValues.Bool(mailbox, "HiddenFromAddressListsEnabled").Should().BeFalse();
        ExchangeValues.Date(mailbox, "WhenMailboxCreated").Should().Be(new DateTimeOffset(2026, 7, 14, 11, 20, 36, TimeSpan.Zero));
        ExchangeValues.Size(mailbox, "ProhibitSendReceiveQuota").Bytes.Should().Be(107_374_182_400L);
        ExchangeValues.Strings(mailbox, "GrantSendOnBehalfTo").Should().BeEmpty();
        ExchangeValues.String(mailbox, "NoSuchProperty").Should().BeNull();
    }
}
