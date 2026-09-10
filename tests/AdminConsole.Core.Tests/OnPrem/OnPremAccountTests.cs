using AdminConsole.Core.OnPrem;
using FluentAssertions;
using Xunit;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>
/// The account-tab operations the AD-only page grew in M12.11: account options, expiry, manager, and resetting a
/// machine account.
/// </summary>
/// <remarks>
/// Each of these is a write ADUC does in one click and that used to need ADUC. The fake enforces the same refusals
/// the domain controller does — a user who is their own manager, a reset on something that is not a computer —
/// because a fake that says yes teaches the calling code to assume yes.
/// </remarks>
public sealed class OnPremAccountTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static (FakeDirectoryService Ad, OnPremUser User, OnPremUser Boss, OnPremComputer Pc) Lab()
    {
        var ad = new FakeDirectoryService();
        var lab = ad.AddOu("Lab");
        var user = ad.AddUser("ada.lovelace", lab.DistinguishedName);
        var boss = ad.AddUser("grace.hopper", lab.DistinguishedName);
        var pc = ad.AddComputer("LAB-WKS-001", lab.DistinguishedName);
        return (ad, user, boss, pc);
    }

    // ---- account options ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Password_never_expires_is_one_bit_and_leaves_the_rest_alone()
    {
        var (ad, user, _, _) = Lab();
        var before = user.UserAccountControl;

        var on = await ad.SetAccountOptionsAsync(user.ObjectGuid, passwordNeverExpires: true, null, Ct);
        on.Success.Should().BeTrue();
        var after = (await ad.GetUserAsync(user.ObjectGuid, Ct))!;
        after.PasswordNeverExpires.Should().BeTrue();
        (after.UserAccountControl & ~AccountFlags.DontExpirePassword).Should().Be(before & ~AccountFlags.DontExpirePassword,
            "every other userAccountControl bit must survive");

        var off = await ad.SetAccountOptionsAsync(user.ObjectGuid, passwordNeverExpires: false, null, Ct);
        off.Success.Should().BeTrue();
        (await ad.GetUserAsync(user.ObjectGuid, Ct))!.PasswordNeverExpires.Should().BeFalse();
    }

    [Fact]
    public async Task Must_change_at_next_logon_can_be_set_and_cleared()
    {
        var (ad, user, _, _) = Lab();

        (await ad.SetAccountOptionsAsync(user.ObjectGuid, null, mustChangePasswordAtNextLogon: true, Ct)).Success.Should().BeTrue();
        (await ad.GetUserAsync(user.ObjectGuid, Ct))!.MustChangePasswordAtNextLogon.Should().BeTrue();

        (await ad.SetAccountOptionsAsync(user.ObjectGuid, null, mustChangePasswordAtNextLogon: false, Ct)).Success.Should().BeTrue();
        (await ad.GetUserAsync(user.ObjectGuid, Ct))!.MustChangePasswordAtNextLogon.Should().BeFalse();
    }

    [Fact]
    public async Task Asking_for_the_options_it_already_has_is_a_no_op_not_a_write()
    {
        var (ad, user, _, _) = Lab();

        var outcome = await ad.SetAccountOptionsAsync(user.ObjectGuid, passwordNeverExpires: false, mustChangePasswordAtNextLogon: false, Ct);

        outcome.Success.Should().BeTrue();
        outcome.WasNoOp.Should().BeTrue();
    }

    [Fact]
    public async Task Null_means_leave_that_option_alone()
    {
        var (ad, user, _, _) = Lab();
        await ad.SetAccountOptionsAsync(user.ObjectGuid, passwordNeverExpires: true, null, Ct);

        // Only touching the other flag must not undo the first.
        await ad.SetAccountOptionsAsync(user.ObjectGuid, null, mustChangePasswordAtNextLogon: true, Ct);

        var after = (await ad.GetUserAsync(user.ObjectGuid, Ct))!;
        after.PasswordNeverExpires.Should().BeTrue();
        after.MustChangePasswordAtNextLogon.Should().BeTrue();
    }

    // ---- expiry -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_expiry_date_is_written_and_read_back_and_null_means_never()
    {
        var (ad, user, _, _) = Lab();
        var when = new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero);

        (await ad.SetAccountExpiryAsync(user.ObjectGuid, when, Ct)).Success.Should().BeTrue();
        var expiring = (await ad.GetUserAsync(user.ObjectGuid, Ct))!;
        expiring.AccountExpires.Should().Be(when);
        expiring.AccountHasExpired.Should().BeFalse();

        (await ad.SetAccountExpiryAsync(user.ObjectGuid, null, Ct)).Success.Should().BeTrue();
        (await ad.GetUserAsync(user.ObjectGuid, Ct))!.AccountExpires.Should().BeNull();
    }

    [Fact]
    public async Task A_date_in_the_past_reads_as_expired()
    {
        var (ad, user, _, _) = Lab();
        await ad.SetAccountExpiryAsync(user.ObjectGuid, DateTimeOffset.UtcNow.AddDays(-1), Ct);

        (await ad.GetUserAsync(user.ObjectGuid, Ct))!.AccountHasExpired.Should().BeTrue();
    }

    // ---- manager ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_manager_is_set_by_guid_and_stored_as_a_distinguished_name()
    {
        var (ad, user, boss, _) = Lab();

        var outcome = await ad.SetManagerAsync(user.ObjectGuid, boss.ObjectGuid, Ct);

        outcome.Success.Should().BeTrue();
        outcome.Summary.Should().Contain("grace.hopper");
        (await ad.GetUserAsync(user.ObjectGuid, Ct))!.ManagerDn.Should().Be(boss.DistinguishedName);
    }

    [Fact]
    public async Task Null_clears_the_manager()
    {
        var (ad, user, boss, _) = Lab();
        await ad.SetManagerAsync(user.ObjectGuid, boss.ObjectGuid, Ct);

        (await ad.SetManagerAsync(user.ObjectGuid, null, Ct)).Success.Should().BeTrue();

        (await ad.GetUserAsync(user.ObjectGuid, Ct))!.ManagerDn.Should().BeNull();
    }

    [Fact]
    public async Task Nobody_can_be_their_own_manager()
    {
        var (ad, user, _, _) = Lab();

        var outcome = await ad.SetManagerAsync(user.ObjectGuid, user.ObjectGuid, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("own manager");
    }

    [Fact]
    public async Task A_manager_that_does_not_exist_is_a_failure_that_names_the_manager()
    {
        var (ad, user, _, _) = Lab();

        var outcome = await ad.SetManagerAsync(user.ObjectGuid, Guid.NewGuid(), Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("manager");
    }

    // ---- reset a computer account ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Resetting_a_computer_account_says_the_machine_must_rejoin()
    {
        // The consequence is the point: the existing secure channel is dead until the machine rejoins, and an admin
        // who did not know that would spend the afternoon on "the trust relationship has failed".
        var (ad, _, _, pc) = Lab();

        var outcome = await ad.ResetComputerAccountAsync(pc.ObjectGuid, Ct);

        outcome.Success.Should().BeTrue();
        outcome.Summary.Should().Contain("rejoin");
    }

    [Fact]
    public async Task Resetting_a_user_this_way_is_refused()
    {
        var (ad, user, _, _) = Lab();

        var outcome = await ad.ResetComputerAccountAsync(user.ObjectGuid, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("computer");
    }
}
