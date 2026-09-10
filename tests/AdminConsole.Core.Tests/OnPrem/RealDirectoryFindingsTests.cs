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

    [Fact]
    public void A_bad_move_destination_comes_back_as_code_80_not_32_or_34()
    {
        // Captured from the lab DC. Both a malformed distinguished name and a container that does not exist return
        // result code 80 with the same diagnostic — not NoSuchObject and not InvalidDnSyntax, which is what the
        // shape of the failure suggests. The table did not know 80 at all, so both fell through to "Active Directory
        // returned error 80", which is the exact message this milestone exists to remove.
        const string diagnostic = "00002089: UpdErr: DSID-031B0ED7, problem 5012 (DIR_ERROR), data 2";

        DirectoryErrorText.Other.Should().Be(80);
        var text = DirectoryErrorText.Friendly(DirectoryErrorText.Other, diagnostic, "that object");
        text.Should().NotContain("returned error");
        text.Should().Contain("destination");
    }

    [Fact]
    public void Every_code_the_table_claims_to_know_has_a_sentence_rather_than_a_number()
    {
        // The check the lab harness makes against a live DC, kept here so it holds without one.
        int[] claimed =
        [
            DirectoryErrorText.NoSuchObject, DirectoryErrorText.EntryAlreadyExists, DirectoryErrorText.InvalidDnSyntax,
            DirectoryErrorText.NotAllowedOnNonLeaf, DirectoryErrorText.ObjectClassViolation,
            DirectoryErrorText.ConstraintViolation, DirectoryErrorText.AttributeOrValueExists,
            DirectoryErrorText.NoSuchAttribute, DirectoryErrorText.InsufficientAccessRights,
            DirectoryErrorText.UnwillingToPerform, DirectoryErrorText.InvalidCredentials,
            DirectoryErrorText.StrongAuthRequired, DirectoryErrorText.Busy, DirectoryErrorText.Unavailable,
            DirectoryErrorText.SizeLimitExceeded, DirectoryErrorText.ReferralCode,
            DirectoryErrorText.OperationsError, DirectoryErrorText.Other,
        ];

        foreach (var code in claimed)
        {
            var text = DirectoryErrorText.Friendly(code, null, "that object");
            text.Should().NotContain("returned error", $"code {code} should have its own sentence");
            text.Length.Should().BeGreaterThan(24, $"code {code} should say something an admin can act on");
        }
    }

    [Fact]
    public async Task A_move_to_a_missing_container_is_refused_before_it_is_sent()
    {
        // Because AD cannot tell the two destination failures apart, the console checks first and says which.
        var ad = new FakeDirectoryService();
        var lab = ad.AddOu("Lab");
        var user = ad.AddUser("ada", lab.DistinguishedName);

        var outcome = await ad.MoveAsync(user.ObjectGuid, "OU=Nowhere,DC=corp,DC=example,DC=com", default);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().NotBeNullOrWhiteSpace();
    }
}
