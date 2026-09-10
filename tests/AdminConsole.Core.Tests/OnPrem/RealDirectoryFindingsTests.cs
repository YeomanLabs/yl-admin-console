using FluentAssertions;
using Xunit;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>
/// The things a real domain controller does that the fake did not (M12.4).
/// </summary>
/// <remarks>
/// Every case here was a passing test against the fake and a failure against `adlab.test`. They are kept as their own
/// file because they are the evidence for a rule this project keeps relearning: a fake proves the logic and cannot
/// prove the semantics, so nothing is "done" until it has met the real thing.
/// </remarks>
public sealed class RealDirectoryFindingsTests
{
    [Fact]
    public void APasswordRefusalCarriesItsCodeAtTheFrontOfTheMessage_NotAfterData()
    {
        // What AD actually returns when a password fails policy. Note "data 0" — the code that explains it is the
        // eight hex digits at the front. Reading only after "data " produced "the server refused the operation",
        // which tells somebody resetting a password nothing at all.
        const string diagnostic = "0000052D: SvcErr: DSID-031A1260, problem 5003 (WILL_NOT_PERFORM), data 0";

        DirectoryErrorText.Win32SubCode(diagnostic).Should().Be("52d");
        DirectoryErrorText.Friendly(DirectoryErrorText.UnwillingToPerform, diagnostic)
            .Should().Contain("does not meet the domain's policy");
    }

    [Fact]
    public void ADataFieldStillWins_WhenItIsNotZero()
    {
        // The other shape, from a failed bind. Both have to work.
        const string diagnostic = "80090308: LdapErr: DSID-0C09042A, comment: AcceptSecurityContext error, data 52e, v3839";

        DirectoryErrorText.Win32SubCode(diagnostic).Should().Be("52e");
        DirectoryErrorText.Friendly(DirectoryErrorText.InvalidCredentials, diagnostic).Should().Contain("password is wrong");
    }

    [Fact]
    public void AMessageWithNeitherCode_FallsBackToTheResultCode()
    {
        DirectoryErrorText.Friendly(DirectoryErrorText.InsufficientAccessRights, "no codes here", "that OU")
            .Should().Contain("per container");
    }

    [Theory]
    [InlineData(DirectoryErrorText.NoSuchAttribute)]
    [InlineData(DirectoryErrorText.EntryAlreadyExists)]
    public void TheCodesThatMeanItIsAlreadyDone_AreNamed(int resultCode)
    {
        // AD reports "clear an attribute that is already absent" as NoSuchAttribute, and "add a member who is
        // already in the group" as EntryAlreadyExists. Both are the wanted state; the service turns them into no-ops
        // rather than showing an admin a failure for something that is true.
        resultCode.Should().BeOneOf(DirectoryErrorText.NoSuchAttribute, DirectoryErrorText.EntryAlreadyExists);
        DirectoryErrorText.Friendly(resultCode).Should().NotBeNullOrWhiteSpace();
    }
}
