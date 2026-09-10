using FluentAssertions;
using Xunit;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>
/// M12.1 — the three things that bite everybody who writes AD code: distinguished-name parsing, the
/// <c>userAccountControl</c> bit field, and turning an LDAP failure into something an admin can act on.
/// </summary>
public sealed class DirectoryPrimitivesTests
{
    // ---- distinguished names -------------------------------------------------------------------------------------

    [Fact]
    public void Split_DoesNotBreakOnAnEscapedComma()
    {
        // The mistake everybody makes. "Smith\, John" is one person, and splitting on commas makes them two.
        var parts = DistinguishedNames.Split(@"CN=Smith\, John,OU=Sales,DC=corp,DC=example");

        parts.Should().Equal([@"CN=Smith\, John", "OU=Sales", "DC=corp", "DC=example"]);
    }

    [Fact]
    public void LeafName_UnescapesWhatItReturns()
    {
        DistinguishedNames.LeafName(@"CN=Smith\, John,OU=Sales,DC=corp").Should().Be("Smith, John");
        DistinguishedNames.LeafName("OU=Sales,DC=corp").Should().Be("Sales");
    }

    [Fact]
    public void Parent_WalksUpOneLevel_AndStopsAtTheRoot()
    {
        DistinguishedNames.Parent("CN=Ada,OU=IT,OU=Lab,DC=adlab,DC=test").Should().Be("OU=IT,OU=Lab,DC=adlab,DC=test");
        DistinguishedNames.Parent("DC=test").Should().BeNull();
        DistinguishedNames.Parent(null).Should().BeNull();
    }

    [Fact]
    public void Friendly_ReadsAsAPath_WithoutTheDomainRepeatedOnEveryRow()
    {
        DistinguishedNames.Friendly("CN=Ada,OU=IT,OU=Users,OU=Lab,DC=adlab,DC=test")
            .Should().Be("Lab / Users / IT");
    }

    [Fact]
    public void DomainOf_ReadsTheDomainComponentsBackAsADnsName()
    {
        DistinguishedNames.DomainOf("CN=Ada,OU=IT,DC=adlab,DC=test").Should().Be("adlab.test");
    }

    [Fact]
    public void Child_EscapesTheName_SoAPunctuatedNameCannotChangeTheStructure()
    {
        var dn = DistinguishedNames.Child("CN", "Smith, John", "OU=Sales,DC=corp");

        dn.Should().Be(@"CN=Smith\, John,OU=Sales,DC=corp");
        DistinguishedNames.Split(dn).Should().HaveCount(3, "the comma in the name must not add a component");
    }

    [Theory]
    [InlineData("CN=Ada,OU=IT,DC=x", "OU=IT,DC=x", true)]
    [InlineData("CN=Ada,OU=IT,DC=x", "DC=x", true)]
    [InlineData("OU=IT,DC=x", "OU=IT,DC=x", false)]          // itself is not under itself
    [InlineData("OU=ITSupport,DC=x", "OU=IT,DC=x", false)]   // a prefix match is not containment
    public void IsUnder_IsAboutContainment_NotStringPrefixes(string dn, string ancestor, bool expected)
    {
        DistinguishedNames.IsUnder(dn, ancestor).Should().Be(expected);
    }

    // ---- userAccountControl --------------------------------------------------------------------------------------

    [Fact]
    public void SetEnabled_LeavesEveryOtherFlagAlone()
    {
        // The classic way to break an account: assigning userAccountControl instead of masking it, which silently
        // clears "password never expires" and "smartcard required" on the way past.
        var before = AccountFlags.NormalAccount | AccountFlags.DontExpirePassword | AccountFlags.SmartcardRequired | AccountFlags.AccountDisabled;

        var after = AccountFlags.SetEnabled(before, enabled: true);

        AccountFlags.IsDisabled(after).Should().BeFalse();
        AccountFlags.PasswordNeverExpires(after).Should().BeTrue("this flag had nothing to do with enabling");
        AccountFlags.SmartcardIsRequired(after).Should().BeTrue("nor did this one");
    }

    [Fact]
    public void SetEnabled_IsReversible()
    {
        var start = AccountFlags.NormalAccount | AccountFlags.DontExpirePassword;

        var round = AccountFlags.SetEnabled(AccountFlags.SetEnabled(start, false), true);

        round.Should().Be(start);
    }

    [Fact]
    public void Describe_NamesTheFlagsThatAreSet_AndNothingElse()
    {
        var value = AccountFlags.NormalAccount | AccountFlags.AccountDisabled | AccountFlags.DontExpirePassword;

        AccountFlags.Describe(value).Should().BeEquivalentTo(["Disabled", "Password never expires"]);
    }

    [Fact]
    public void ANewUserIsCreatedDisabled_BecauseAdRefusesAnEnabledAccountWithNoPassword()
    {
        AccountFlags.IsDisabled(AccountFlags.NewDisabledUser).Should().BeTrue();
        AccountFlags.IsDisabled(AccountFlags.NewEnabledUser).Should().BeFalse();
    }

    // ---- error translation ---------------------------------------------------------------------------------------

    [Fact]
    public void Win32SubCode_IsPulledOutOfTheDiagnosticMessage()
    {
        // The result code says "constraint violation"; the sub-code says which one, and only the sub-code is useful.
        DirectoryErrorText.Win32SubCode("80090308: LdapErr: DSID-0C09042A, comment: AcceptSecurityContext error, data 52e, v3839")
            .Should().Be("52e");
        DirectoryErrorText.Win32SubCode("no code in here").Should().BeNull();
        DirectoryErrorText.Win32SubCode(null).Should().BeNull();
    }

    [Fact]
    public void TheSubCodeBeatsTheResultCode_BecauseItIsTheOneThatExplainsAnything()
    {
        var text = DirectoryErrorText.Friendly(
            DirectoryErrorText.ConstraintViolation,
            "0000052D: SvcErr: DSID-031A126C, problem 5003 (WILL_NOT_PERFORM), data 52d");

        text.Should().Contain("does not meet the domain's policy");
    }

    [Fact]
    public void InsufficientRights_SaysWhyHavingRightsElsewhereDoesNotHelp()
    {
        // AD delegation is per-container, and that is exactly the thing an admin gets wrong.
        var text = DirectoryErrorText.Friendly(DirectoryErrorText.InsufficientAccessRights, null, "that OU");

        text.Should().Contain("per container");
    }

    [Fact]
    public void StrongAuthRequired_NamesLdaps_BecauseThatIsTheCommonestSetupFailure()
    {
        DirectoryErrorText.Friendly(DirectoryErrorText.StrongAuthRequired)
            .Should().Contain("636");
    }

    [Fact]
    public void NotAllowedOnNonLeaf_TellsThemToEmptyItFirst()
    {
        DirectoryErrorText.Friendly(DirectoryErrorText.NotAllowedOnNonLeaf)
            .Should().Contain("Empty it");
    }

    [Fact]
    public void AnUnknownCode_StillSaysSomethingRatherThanNothing()
    {
        var text = DirectoryErrorText.Friendly(9999, "something odd happened, data ffff");

        text.Should().Contain("9999");
    }
}
