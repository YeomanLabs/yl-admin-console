using FluentAssertions;
using Xunit;
using AdminConsole.Core.Templates;

namespace AdminConsole.Core.Tests.Templates;

/// <summary>
/// M7.1: rule expansion. A wrong UPN is the one mistake in a create flow that cannot be quietly corrected afterwards,
/// so this is pinned closely.
/// </summary>
public sealed class NameRulesTests
{
    [Theory]
    [InlineData("{first}.{last}@{domain}", "Jane", "Doe", "jane.doe@contoso.com")]
    [InlineData("{f}{last}@{domain}", "Jane", "Doe", "jdoe@contoso.com")]
    [InlineData("{first}{l}@{domain}", "Jane", "Doe", "janed@contoso.com")]
    [InlineData("{last}.{first}@{domain}", "Jane", "Doe", "doe.jane@contoso.com")]
    [InlineData("{first}_{last}@{domain}", "Jane", "Doe", "jane_doe@contoso.com")]
    public void ExpandAddress_BuildsTheUpnTheRuleAsksFor(string rule, string first, string last, string expected)
    {
        NameRules.ExpandAddress(rule, first, last, "contoso.com").Should().Be(expected);
    }

    [Fact]
    public void ExpandAddress_FoldsAccentsAndDropsPunctuationEntraWouldReject()
    {
        NameRules.ExpandAddress("{first}.{last}@{domain}", "Zoë", "O'Brien", "contoso.com")
            .Should().Be("zoe.obrien@contoso.com");
    }

    [Theory]
    [InlineData("  Jane  ", "  Doe  ", "jane.doe@contoso.com")]
    [InlineData("Mary Jane", "Van Der Berg", "maryjane.vanderberg@contoso.com")]
    public void ExpandAddress_TrimsAndClosesUpSpaces(string first, string last, string expected)
    {
        NameRules.ExpandAddress("{first}.{last}@{domain}", first, last, "contoso.com").Should().Be(expected);
    }

    [Fact]
    public void ExpandAddress_WithAMissingName_DoesNotLeaveADanglingSeparator()
    {
        // Half a form filled in must not produce ".doe@contoso.com".
        NameRules.ExpandAddress("{first}.{last}@{domain}", string.Empty, "Doe", "contoso.com").Should().Be("doe@contoso.com");
        NameRules.ExpandAddress("{first}.{last}@{domain}", "Jane", string.Empty, "contoso.com").Should().Be("jane@contoso.com");
    }

    [Fact]
    public void ExpandAddress_ForAMailNickname_LeavesOutTheDomain()
    {
        NameRules.ExpandAddress("{first}.{last}", "Jane", "Doe").Should().Be("jane.doe");
    }

    [Fact]
    public void ExpandDisplay_KeepsTheNameAsWritten_AccentsIncluded()
    {
        // A person called Zoë should be shown as Zoë; only the address forms are folded.
        NameRules.ExpandDisplay("{First} {Last}", "zoë", "o'brien").Should().Be("Zoë O'brien");
        NameRules.ExpandDisplay("{Last}, {First}", "Jane", "Doe").Should().Be("Doe, Jane");
    }

    [Fact]
    public void Expand_AnUnknownToken_IsLeftVisible_NotSilentlyEmptied()
    {
        // An admin who mistypes {frist} should see it, not get a UPN with a hole in it.
        NameRules.ExpandAddress("{frist}.{last}@{domain}", "Jane", "Doe", "contoso.com").Should().Contain("frist");
    }

    [Fact]
    public void Expand_AnUnclosedBrace_IsLeftVisibleToo()
    {
        NameRules.ExpandDisplay("{First} {Last", "Jane", "Doe").Should().Be("Jane {Last");
    }

    [Theory]
    [InlineData("...jane...doe...", "jane.doe")]
    [InlineData("-jane-", "jane")]
    [InlineData("JANE.DOE", "jane.doe")]
    public void SanitiseAddress_CollapsesRepeatsAndTrimsSeparators(string input, string expected)
    {
        NameRules.SanitiseAddress(input).Should().Be(expected);
    }

    [Fact]
    public void SanitiseAddress_LeavesTheDomainAlone()
    {
        NameRules.SanitiseAddress("Jane.Doe@Contoso.COM").Should().Be("jane.doe@contoso.com");
    }
}
