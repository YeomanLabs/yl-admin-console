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

    [Fact]
    public void NoSuchAttribute_means_two_different_things_and_the_leading_code_separates_them()
    {
        // Both of these are LDAP 16, captured from the lab DC. The first is a replace onto mailNickname in a forest
        // with no Exchange schema; the second is a delete of an attribute that is simply not set on the object.
        // Reported identically, a schema gap read as "that value was not there to remove", which sends an admin
        // hunting for a value that was never the problem.
        const string noSuchAttributeInSchema = "00000057: LdapErr: DSID-0C090FDF, comment: Error in attribute conversion operation, data 0, v4f7c";
        const string notSetOnThisObject = "00002076: AtrErr: DSID-030F1D1F, #1:";

        DirectoryErrorText.Friendly(DirectoryErrorText.NoSuchAttribute, noSuchAttributeInSchema)
            .Should().Contain("schema");
        DirectoryErrorText.Friendly(DirectoryErrorText.NoSuchAttribute, notSetOnThisObject)
            .Should().Contain("not there to remove");
    }

    [Fact]
    public async Task Clearing_an_absent_attribute_is_a_no_op_but_writing_an_absent_one_is_a_failure()
    {
        // The lab DC answers 16 to both, so the result code cannot tell them apart — only the operation can. Treating
        // 16 as benign for every modify reported a write that never happened as a successful no-op, and the identity
        // sheet would then have shown a tick over a value the directory did not have.
        var ad = new FakeDirectoryService();
        var user = ad.AddUser("probe", ad.AddOu("Lab").DistinguishedName);

        var clear = await ad.SetAttributesAsync(user.ObjectGuid, new Dictionary<string, string?> { ["department"] = null }, default);
        clear.Success.Should().BeTrue();

        var again = await ad.SetAttributesAsync(user.ObjectGuid, new Dictionary<string, string?> { ["department"] = null }, default);
        again.Success.Should().BeTrue();
    }

    [Fact]
    public async Task The_directory_says_which_attributes_are_writable_so_a_field_never_unlocks_into_a_refusal()
    {
        // allowedAttributesEffective answers both "is it in this schema" and "may this admin write it". The lab DC
        // returned 319 attributes for a seeded user and correctly omitted mailNickname.
        var ad = new FakeDirectoryService { WritableAttributes = ["title", "department"] };
        var user = ad.AddUser("probe", ad.AddOu("Lab").DistinguishedName);

        var writable = await ad.GetWritableAttributesAsync(user.ObjectGuid, default);
        writable.Should().NotBeNull();
        writable!.Should().Contain("title").And.NotContain("mailNickname");
    }
}
