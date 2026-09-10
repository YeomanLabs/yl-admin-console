using AdminConsole.Core.OnPrem;
using FluentAssertions;
using Xunit;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>
/// The decision behind the hybrid unlock (M12.4): whether a synced user's mastered attribute can be edited here, and
/// what to say when it cannot.
/// </summary>
/// <remarks>
/// Four separate things have to be true for a field to unlock, and each one failing puts the admin somewhere
/// different: off the network, looking at an account Entra Connect has stopped syncing, at a forest without the
/// attribute, or without the delegation. Phase 1 answered all four with the same sentence because it could do
/// nothing about any of them. Now that it can, the same sentence would be a lie in three cases out of four.
/// </remarks>
public sealed class HybridUnlockTests
{
    private static readonly DirectoryStatus Connected = new()
    {
        IsConnected = true,
        DomainName = "corp.example.com",
        DomainController = "DC01.corp.example.com",
        RootDn = "DC=corp,DC=example,DC=com",
        BoundAs = "CORP\\admin",
    };

    private static readonly OnPremUser Found = new()
    {
        ObjectGuid = Guid.NewGuid(),
        DistinguishedName = "CN=Ada Lovelace,OU=Sales,OU=Users,DC=corp,DC=example,DC=com",
        SamAccountName = "ada.lovelace",
        UserAccountControl = AccountFlags.NormalAccount,
    };

    [Fact]
    public void Unlocks_when_the_dc_answers_the_object_is_found_and_the_attribute_is_writable()
    {
        var state = HybridUnlock.ForField("jobTitle", Connected, onPremFound: true, writable: new HashSet<string> { "title" });

        state.CanEdit.Should().BeTrue();
        state.LdapName.Should().Be("title");
        state.Caption.Should().Contain("title");
    }

    [Fact]
    public void Off_the_network_is_a_stated_reason_rather_than_a_failure()
    {
        // A laptop away from the office is the normal case, not an error, and it should read as one.
        var state = HybridUnlock.ForField("jobTitle", DirectoryStatus.Unreachable("The server is not operational."), true, null);

        state.CanEdit.Should().BeFalse();
        state.Caption.Should().Contain("no domain controller reachable");
    }

    [Fact]
    public void A_missing_ad_object_says_so_rather_than_blaming_the_network()
    {
        // The DC answered; nothing matched the source anchor. That means the sync, not the connection.
        var state = HybridUnlock.ForField("jobTitle", Connected, onPremFound: false, writable: null);

        state.CanEdit.Should().BeFalse();
        state.Caption.Should().Contain("AD object was not found");
    }

    [Fact]
    public void An_attribute_this_forest_lacks_stays_locked_and_names_itself()
    {
        // mailNickname does not exist without the Exchange schema extension. Unlocking it would offer an edit that
        // the directory refuses the moment it is attempted — which is exactly what happened before this check.
        var state = HybridUnlock.ForField("mailNickname", Connected, true, new HashSet<string> { "title", "department" });

        state.CanEdit.Should().BeFalse();
        state.Caption.Should().Contain("mailNickname");
        state.Caption.Should().Contain("schema");
    }

    [Fact]
    public void An_unanswered_writable_set_means_unknown_not_none()
    {
        // Some directories do not return allowedAttributesEffective. Locking everything on that basis would make the
        // feature vanish on a domain where it would have worked; the write's own refusal is the fallback.
        var state = HybridUnlock.ForField("jobTitle", Connected, true, writable: null);

        state.CanEdit.Should().BeTrue();
    }

    [Fact]
    public void Manager_stays_locked_even_when_everything_else_is_in_place()
    {
        var state = HybridUnlock.ForField("manager", Connected, true, new HashSet<string> { "manager" });

        state.CanEdit.Should().BeFalse();
        state.Caption.Should().Contain("distinguished name");
    }

    [Fact]
    public void Every_outcome_carries_a_reason()
    {
        // The one rule that must hold across all of them: a greyed control never appears without an explanation.
        var cases = new[]
        {
            HybridUnlock.ForField("jobTitle", DirectoryStatus.NotConfigured, false, null),
            HybridUnlock.ForField("jobTitle", Connected, false, null),
            HybridUnlock.ForField("mailNickname", Connected, true, new HashSet<string>()),
            HybridUnlock.ForField("manager", Connected, true, null),
            HybridUnlock.ForField("jobTitle", Connected, true, null),
        };

        cases.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Caption));
    }

    [Fact]
    public void The_sheet_caption_does_not_claim_an_answer_before_the_probe_has_run()
    {
        HybridUnlock.ForSheet(DirectoryStatus.NotConfigured, null, probed: false)
            .Should().Contain("Checking");
    }

    [Fact]
    public void The_sheet_caption_names_the_controller_and_the_ou_once_unlocked()
    {
        var caption = HybridUnlock.ForSheet(Connected, Found, probed: true);

        caption.Should().Contain("corp.example.com");
        caption.Should().Contain("DC01.corp.example.com");
        // The readable path, not the distinguished name: "which OU is this in" is the question being answered.
        caption.Should().Contain("Sales");
        caption.Should().NotContain("CN=");
    }

    [Fact]
    public void The_sheet_caption_repeats_the_connection_reason_when_there_is_none()
    {
        var caption = HybridUnlock.ForSheet(DirectoryStatus.Unreachable("The server is not operational."), null, probed: true);

        caption.Should().Contain("The server is not operational.");
    }
}
