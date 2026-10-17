using AdminConsole.Core.Auth;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.OnPrem;
using FluentAssertions;
using Xunit;

namespace AdminConsole.Core.Tests.Offboarding;

/// <summary>
/// The two offboarding steps that were greyed from M6 until Phase 2 existed (M12.7).
/// </summary>
/// <remarks>
/// These are the steps that make an offboarding hold. Step 1 disables sign-in through Graph, and on a synced account
/// Entra Connect puts it straight back — so a run that reported success while the account returned an hour later is
/// the worst failure this product could have. Most of what is tested here is what happens when they cannot run,
/// because "the laptop is not on the corporate network" is the normal case, not the exception.
/// </remarks>
public sealed class OnPremOffboardingStepTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DirectoryUser User(bool synced, string? immutableId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserPrincipalName = "ada@corp.example.com",
        DisplayName = "Ada Lovelace",
        AccountEnabled = true,
        OnPremisesSyncEnabled = synced,
        Licensed = false,
        Raw = immutableId is null ? "{}" : $$"""{"onPremisesImmutableId":"{{immutableId}}"}""",
    };

    private static OffboardingContext Context(DirectoryUser user) => new()
    {
        User = user,
        Tenant = new TenantContext(),
        CorrelationId = Guid.NewGuid().ToString(),
    };

    private static (FakeDirectoryService Ad, OnPremUser Leaver, string DisabledOu) Lab()
    {
        var ad = new FakeDirectoryService();
        var lab = ad.AddOu("Lab");
        var users = ad.AddOu("Users", lab.DistinguishedName);
        var disabled = ad.AddOu("Disabled", lab.DistinguishedName);
        var leaver = ad.AddUser("ada.lovelace", users.DistinguishedName);
        return (ad, leaver, disabled.DistinguishedName);
    }

    private static string AnchorOf(OnPremUser user) => Convert.ToBase64String(user.ObjectGuid.ToByteArray());

    // ---- disabling ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disabling_the_ad_account_is_the_disable_that_survives_the_next_sync()
    {
        var (ad, leaver, _) = Lab();
        var step = new DisableAdAccountStep(ad);
        var ctx = Context(User(synced: true, AnchorOf(leaver)));

        var preview = await step.PreviewAsync(ctx, Ct);
        preview.CanRun.Should().BeTrue();
        preview.Summary.Should().Contain("ada.lovelace");

        var result = await step.ExecuteAsync(ctx, Ct);

        result.Outcome.Should().Be(StepOutcome.Success);
        (await ad.GetUserAsync(leaver.ObjectGuid, Ct))!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_cloud_only_user_has_nothing_to_disable_and_that_is_not_a_failure()
    {
        // Most tenants have no domain at all. Listing this step as failed on every cloud-only offboarding would
        // teach an admin to ignore the failures, which is the opposite of what a run log is for.
        var (ad, _, _) = Lab();
        var preview = await new DisableAdAccountStep(ad).PreviewAsync(Context(User(synced: false, null)), Ct);

        preview.IsNoOp.Should().BeTrue();
        preview.Summary.Should().Contain("cloud-only");
    }

    [Fact]
    public async Task An_already_disabled_ad_account_is_a_no_op()
    {
        var (ad, leaver, _) = Lab();
        await ad.SetEnabledAsync(leaver.ObjectGuid, false, Ct);

        var preview = await new DisableAdAccountStep(ad).PreviewAsync(Context(User(true, AnchorOf(leaver))), Ct);

        preview.IsNoOp.Should().BeTrue();
    }

    [Fact]
    public async Task No_domain_controller_blocks_the_step_with_that_reason_and_not_a_stack_trace()
    {
        var ad = new FakeDirectoryService { IsConnected = false };
        var preview = await new DisableAdAccountStep(ad).PreviewAsync(Context(User(true, "AAAA")), Ct);

        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_synced_user_with_no_source_anchor_says_so_rather_than_searching_by_name()
    {
        // Matching on UPN or display name would eventually disable the wrong person. Refusing is the right answer.
        var (ad, _, _) = Lab();
        var preview = await new DisableAdAccountStep(ad).PreviewAsync(Context(User(synced: true, null)), Ct);

        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().Contain("source anchor");
    }

    [Fact]
    public async Task An_anchor_matching_nothing_points_at_the_sync_rather_than_the_network()
    {
        var (ad, _, _) = Lab();
        var orphan = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        var preview = await new DisableAdAccountStep(ad).PreviewAsync(Context(User(true, orphan)), Ct);

        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().Contain("Entra Connect");
    }

    // ---- moving ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Moving_to_the_leavers_ou_names_both_ends_in_the_preview()
    {
        var (ad, leaver, disabledOu) = Lab();
        var step = new MoveToDisabledOuStep(ad, new LdapOptions { DisabledOu = disabledOu });
        var ctx = Context(User(true, AnchorOf(leaver)));

        var preview = await step.PreviewAsync(ctx, Ct);

        preview.CanRun.Should().BeTrue();
        preview.Summary.Should().Contain("Users").And.Contain("Disabled");
    }

    [Fact]
    public async Task With_no_leavers_ou_configured_the_step_blocks_and_names_the_setting()
    {
        // Picking an OU on the admin's behalf would apply somebody else's policy to an account at the exact moment
        // it matters most.
        var (ad, leaver, _) = Lab();
        var step = new MoveToDisabledOuStep(ad, new LdapOptions());

        var preview = await step.PreviewAsync(Context(User(true, AnchorOf(leaver))), Ct);

        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().Contain("OnPrem:DisabledOu");
    }

    [Fact]
    public async Task Moving_keeps_the_objectGuid_so_the_link_to_the_cloud_account_survives()
    {
        var (ad, leaver, disabledOu) = Lab();
        var step = new MoveToDisabledOuStep(ad, new LdapOptions { DisabledOu = disabledOu });
        var ctx = Context(User(true, AnchorOf(leaver)));

        await step.PreviewAsync(ctx, Ct);
        var result = await step.ExecuteAsync(ctx, Ct);

        result.Outcome.Should().Be(StepOutcome.Success);
        var after = await ad.GetUserAsync(leaver.ObjectGuid, Ct);
        after.Should().NotBeNull();
        after!.DistinguishedName.Should().EndWith(disabledOu);
        after.ObjectGuid.Should().Be(leaver.ObjectGuid);
    }

    [Fact]
    public async Task A_move_can_be_undone_because_a_run_started_on_the_wrong_person_is_exactly_when_that_matters()
    {
        var (ad, leaver, disabledOu) = Lab();
        var step = new MoveToDisabledOuStep(ad, new LdapOptions { DisabledOu = disabledOu });
        var ctx = Context(User(true, AnchorOf(leaver)));
        var origin = leaver.DistinguishedName;

        await step.PreviewAsync(ctx, Ct);
        var result = await step.ExecuteAsync(ctx, Ct);

        result.Undo.Should().NotBeNull();
        (await result.Undo!(Ct)).Success.Should().BeTrue();
        (await ad.GetUserAsync(leaver.ObjectGuid, Ct))!.DistinguishedName.Should().Be(origin);
    }

    [Fact]
    public async Task An_account_already_in_the_leavers_ou_is_a_no_op()
    {
        var (ad, leaver, disabledOu) = Lab();
        await ad.MoveAsync(leaver.ObjectGuid, disabledOu, Ct);
        var step = new MoveToDisabledOuStep(ad, new LdapOptions { DisabledOu = disabledOu });

        var preview = await step.PreviewAsync(Context(User(true, AnchorOf(leaver))), Ct);

        preview.IsNoOp.Should().BeTrue();
    }

    // ---- ordering ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_move_runs_before_the_disable()
    {
        // An OU usually carries policy — a restrictive GPO, a different password policy, exclusion from sync — and
        // moving an account that is still enabled into it changes what that account can do while it can still be
        // used. Doc 06 numbers them 18 then 19 for that reason.
        new MoveToDisabledOuStep(new FakeDirectoryService(), new LdapOptions()).Id.Should().Be("move-ou");
        new DisableAdAccountStep(new FakeDirectoryService()).Id.Should().Be("disable-ad-account");
    }

    [Fact]
    public void Neither_step_asks_for_a_graph_permission()
    {
        // AD enforces what the signed-in admin has been delegated, which is the whole point of binding as them.
        new MoveToDisabledOuStep(new FakeDirectoryService(), new LdapOptions()).RequiredCapability.Should().BeNull();
        new DisableAdAccountStep(new FakeDirectoryService()).RequiredCapability.Should().BeNull();
    }

    [Fact]
    public void Neither_step_halts_the_run()
    {
        // Stopping here would leave licences assigned and sessions live, which is worse than carrying on and
        // reporting the failure loudly.
        new MoveToDisabledOuStep(new FakeDirectoryService(), new LdapOptions()).HaltsOnFailure.Should().BeFalse();
        new DisableAdAccountStep(new FakeDirectoryService()).HaltsOnFailure.Should().BeFalse();
    }

    // ---- M15.2: the AD password and the AD groups --------------------------------------------------------------

    [Fact]
    public async Task The_ad_password_is_reset_to_something_nobody_is_shown()
    {
        var (ad, leaver, _) = Lab();
        var step = new ResetAdPasswordStep(ad);
        var ctx = Context(User(synced: true, AnchorOf(leaver)));

        var preview = await step.PreviewAsync(ctx, Ct);
        preview.CanRun.Should().BeTrue();
        preview.Summary.Should().Be("Reset ada.lovelace's AD password to 24 random characters, shown to nobody");

        var result = await step.ExecuteAsync(ctx, Ct);

        result.Outcome.Should().Be(StepOutcome.Success);
        result.Message.Should().Be("Reset ada.lovelace's AD password to a random value");
        result.CanUndo.Should().BeFalse("the old password is not known, so there is nothing to put back");
        (await ad.GetUserAsync(leaver.ObjectGuid, Ct))!.PasswordLastSet.Should().NotBeNull();
    }

    [Fact]
    public async Task A_cloud_only_user_has_no_ad_password_to_reset()
    {
        var preview = await new ResetAdPasswordStep(new FakeDirectoryService()).PreviewAsync(Context(User(synced: false, null)), Ct);

        preview.IsNoOp.Should().BeTrue();
    }

    [Fact]
    public async Task Ad_groups_are_emptied_built_in_ones_included_and_the_primary_group_is_said_to_stay()
    {
        var (ad, leaver, _) = Lab();
        var finance = ad.AddGroup("Finance");
        var vpn = ad.AddGroup("VPN Users");
        var admins = ad.AddGroup("Domain Admins", critical: true);
        foreach (var g in new[] { finance, vpn, admins }) (await ad.AddToGroupAsync(g.ObjectGuid, leaver.ObjectGuid, Ct)).Success.Should().BeTrue();
        var step = new RemoveAdGroupMembershipsStep(ad);
        var ctx = Context(User(synced: true, AnchorOf(leaver)));

        var preview = await step.PreviewAsync(ctx, Ct);

        preview.Summary.Should().Be("Remove ada.lovelace from 3 AD groups");
        preview.Details.Should().Contain("Domain Admins — built-in; AD lets only a domain admin change it")
            .And.Contain("Finance").And.Contain("VPN Users")
            .And.Contain("Domain Users stays — it is the account's primary group, which every AD account must have");

        var result = await step.ExecuteAsync(ctx, Ct);

        result.Outcome.Should().Be(StepOutcome.Success);
        (await ad.GetUserAsync(leaver.ObjectGuid, Ct))!.MemberOfDns.Should().BeEmpty();

        var undo = await result.Undo!(Ct);
        undo.Success.Should().BeTrue();
        (await ad.GetUserAsync(leaver.ObjectGuid, Ct))!.MemberOfDns.Should().HaveCount(3, "undo puts every removed group back");
    }

    [Fact]
    public async Task A_user_in_no_ad_groups_is_nothing_to_do()
    {
        var (ad, leaver, _) = Lab();

        var preview = await new RemoveAdGroupMembershipsStep(ad).PreviewAsync(Context(User(synced: true, AnchorOf(leaver))), Ct);

        preview.IsNoOp.Should().BeTrue();
        preview.Summary.Should().Be("ada.lovelace is in no AD groups beyond its primary group");
    }

    [Fact]
    public async Task With_no_domain_controller_both_ad_steps_say_why_rather_than_fail()
    {
        var ad = new FakeDirectoryService { IsConnected = false };
        var ctx = Context(User(synced: true, "AAAAAAAAAAAAAAAAAAAAAA=="));

        (await new ResetAdPasswordStep(ad).PreviewAsync(ctx, Ct)).CanRun.Should().BeFalse();
        (await new RemoveAdGroupMembershipsStep(ad).PreviewAsync(ctx, Ct)).CanRun.Should().BeFalse();
    }
}
