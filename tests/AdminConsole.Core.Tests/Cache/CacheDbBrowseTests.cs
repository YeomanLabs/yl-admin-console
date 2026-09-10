using FluentAssertions;
using Xunit;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Cache.Browse;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Tests.Cache;

/// <summary>M9.1: the reads behind the Users, Groups and Devices list pages.</summary>
public sealed class CacheDbBrowseTests : IDisposable
{
    private readonly CacheDb _db = CacheDb.OpenInMemory();

    public void Dispose() => _db.Dispose();

    // ---- users -------------------------------------------------------------------------------------------------

    [Fact]
    public void BrowseUsers_WithNoQuery_ReturnsEveryoneByName()
    {
        SeedUsers();

        var page = _db.BrowseUsers(new BrowseQuery());

        page.Total.Should().Be(4);
        page.Rows.Select(r => r.DisplayName).Should().Equal(["Ada Lovelace", "Bea Guest", "Cy Disabled", "Dee Unlicensed"],
            "the default sort is by display name ascending");
    }

    [Fact]
    public void BrowseUsers_Pages_AndReportsTheTotalBehindThePage()
    {
        SeedUsers();

        var page = _db.BrowseUsers(new BrowseQuery { Take = 2, Skip = 2 });

        page.Rows.Should().HaveCount(2);
        page.Total.Should().Be(4, "the total is what the query matched, not what this page holds");
        page.Skip.Should().Be(2);
        page.HasMore.Should().BeFalse();
        _db.BrowseUsers(new BrowseQuery { Take = 2 }).HasMore.Should().BeTrue();
    }

    [Fact]
    public void BrowseUsers_FiltersInTheSameGroup_AreOred()
    {
        SeedUsers();

        // Ticking Enabled and Disabled together must widen to everyone, not collapse to nobody — a chip filter that
        // empties itself when you tick a second box reads as broken.
        var both = _db.BrowseUsers(new BrowseQuery { FilterKeys = ["enabled", "disabled"] });

        both.Total.Should().Be(4);
        _db.BrowseUsers(new BrowseQuery { FilterKeys = ["disabled"] }).Rows.Single().DisplayName.Should().Be("Cy Disabled");
    }

    [Fact]
    public void BrowseUsers_FiltersInDifferentGroups_AreAnded()
    {
        SeedUsers();

        _db.BrowseUsers(new BrowseQuery { FilterKeys = ["enabled", "unlicensed"] })
            .Rows.Select(r => r.DisplayName).Should().Equal(["Bea Guest", "Dee Unlicensed"],
                "enabled AND unlicensed — the guest counts, because a guest holds no licence either");

        // A third group narrows again: the leaver-still-active question is about staff, not guests.
        _db.BrowseUsers(new BrowseQuery { FilterKeys = ["enabled", "unlicensed", "members"] })
            .Rows.Select(r => r.DisplayName).Should().Equal(["Dee Unlicensed"]);
    }

    [Fact]
    public void BrowseUsers_UnknownFilterKey_IsIgnoredRatherThanThrowing()
    {
        SeedUsers();

        // A saved view should survive a filter being renamed rather than failing to open.
        _db.BrowseUsers(new BrowseQuery { FilterKeys = ["disabled", "no-such-filter"] })
            .Rows.Single().DisplayName.Should().Be("Cy Disabled");
    }

    [Fact]
    public void BrowseUsers_TextMatchesTheMiddleOfAWord_WhichFtsCannot()
    {
        SeedUsers();

        _db.BrowseUsers(new BrowseQuery { Text = "ovela" }).Rows.Single().DisplayName.Should().Be("Ada Lovelace");
    }

    [Fact]
    public void BrowseUsers_TextSearchesTheColumnsAnAdminWouldType()
    {
        SeedUsers();

        _db.BrowseUsers(new BrowseQuery { Text = "Engineering" }).Rows.Single().DisplayName.Should().Be("Ada Lovelace");
        _db.BrowseUsers(new BrowseQuery { Text = "cy@" }).Rows.Single().DisplayName.Should().Be("Cy Disabled");
    }

    [Fact]
    public void BrowseUsers_AWildcardTypedIntoTheBox_IsALiteral()
    {
        SeedUsers();

        // Without ESCAPE, typing % would match every row and look like the filter had silently stopped working.
        _db.BrowseUsers(new BrowseQuery { Text = "%" }).Total.Should().Be(0);
        _db.BrowseUsers(new BrowseQuery { Text = "_" }).Total.Should().Be(0);
    }

    [Fact]
    public void BrowseUsers_TextAndFilters_ApplyTogether()
    {
        SeedUsers();

        _db.BrowseUsers(new BrowseQuery { Text = "e", FilterKeys = ["disabled"] })
            .Rows.Single().DisplayName.Should().Be("Cy Disabled");
    }

    [Fact]
    public void BrowseUsers_SortsByTheChosenColumn_InBothDirections()
    {
        SeedUsers();

        _db.BrowseUsers(new BrowseQuery { SortKey = "upn", Descending = true })
            .Rows.First().Upn.Should().Be("dee@contoso.com");
    }

    [Fact]
    public void BrowseUsers_RowsWithNothingInTheSortColumn_GoLastEitherWay()
    {
        SeedUsers();

        // Bea and Cy have no job title. A blank is the least useful thing to put at the top of a list, whichever way
        // the header was clicked.
        _db.BrowseUsers(new BrowseQuery { SortKey = "job" }).Rows.Take(2)
            .Should().OnlyContain(r => r.JobTitle != null);
        _db.BrowseUsers(new BrowseQuery { SortKey = "job", Descending = true }).Rows.Take(2)
            .Should().OnlyContain(r => r.JobTitle != null);
    }

    [Fact]
    public void BrowseUsers_ProjectsTheFlagsTheListDraws()
    {
        SeedUsers();

        var bea = _db.BrowseUsers(new BrowseQuery { Text = "Bea" }).Rows.Single();

        bea.IsGuest.Should().BeTrue();
        bea.Enabled.Should().BeTrue();
        bea.Licensed.Should().BeFalse();
        bea.Synced.Should().BeFalse();
    }

    [Fact]
    public void UserListRow_WithNoDisplayName_FallsBackToTheSignInName()
    {
        _db.Write(w => w.UpsertUser(new DirectoryUser
        {
            Id = "u9", UserPrincipalName = "nameless@contoso.com", DisplayName = null,
            AccountEnabled = true, OnPremisesSyncEnabled = false, Licensed = false, Raw = "{}",
        }));

        _db.BrowseUsers(new BrowseQuery()).Rows.Single().Primary.Should().Be("nameless@contoso.com",
            "a row must never render blank");
    }

    [Fact]
    public void CountUserFilters_CountsEachChipAgainstTheOtherGroups_NotItsOwn()
    {
        SeedUsers();

        var counts = _db.CountUserFilters(new BrowseQuery { FilterKeys = ["enabled"] });

        counts["disabled"].Should().Be(1, "ticking Disabled alongside Enabled would add Cy, so the chip says 1");
        counts["unlicensed"].Should().Be(2, "counted within the current Enabled filter: Bea and Dee");
    }

    // ---- groups ------------------------------------------------------------------------------------------------

    [Fact]
    public void BrowseGroups_FiltersOnWhatMakesAGroupEditable()
    {
        SeedGroups();

        _db.BrowseGroups(new BrowseQuery { FilterKeys = ["dynamic"] }).Rows.Single().DisplayName.Should().Be("All Staff");
        _db.BrowseGroups(new BrowseQuery { FilterKeys = ["empty"] }).Rows.Single().DisplayName.Should().Be("Old Project");
        _db.BrowseGroups(new BrowseQuery { SortKey = "members", Descending = true })
            .Rows.First().MemberCount.Should().Be(42);
    }

    // ---- devices -----------------------------------------------------------------------------------------------

    [Fact]
    public void BrowseDevices_UnionsIntuneAndEntra_AndSaysWhichIsWhich()
    {
        SeedDevices();

        var rows = _db.BrowseDevices(new BrowseQuery()).Rows;

        rows.Should().HaveCount(3, "two Intune devices and the one Entra device that was never enrolled");
        rows.Single(r => r.DeviceName == "REG-ONLY").Source.Should().Be(DeviceSource.Entra);
        rows.Single(r => r.DeviceName == "REG-ONLY").Serial.Should().BeNull("Entra knows no serial");
        rows.Single(r => r.DeviceName == "REG-ONLY").Compliance.Should().BeNull("Intune has never evaluated it");
    }

    [Fact]
    public void BrowseDevices_ADeviceInBothDirectories_AppearsOnce_FromIntune()
    {
        SeedDevices();

        var rows = _db.BrowseDevices(new BrowseQuery { Text = "LT-01" }).Rows;

        rows.Should().HaveCount(1, "the Entra row for the same device must not double it");
        rows[0].Source.Should().Be(DeviceSource.Intune, "the Intune row is the one with a serial and something to act on");
        rows[0].TrustType.Should().Be("AzureAd", "and it is enriched with what Entra knows");
    }

    [Fact]
    public void BrowseDevices_EntraOnlyFilter_FindsWhatIsRegisteredButNotEnrolled()
    {
        SeedDevices();

        _db.BrowseDevices(new BrowseQuery { FilterKeys = ["entraonly"] })
            .Rows.Single().DeviceName.Should().Be("REG-ONLY");
    }

    [Fact]
    public void BrowseDevices_ComplianceUnknown_CoversTheNullsAsWellAsTheOddStates()
    {
        SeedDevices();

        // An Entra-only device has no compliance state at all. "Unknown" has to mean that too, or the filters do not
        // add up to the whole list and the admin cannot tell where the missing rows went.
        _db.BrowseDevices(new BrowseQuery { FilterKeys = ["unknown"] })
            .Rows.Select(r => r.DeviceName).Should().Contain("REG-ONLY");
    }

    [Fact]
    public void BrowseDevices_StaleFilter_IsRelativeToNow()
    {
        SeedDevices();

        _db.BrowseDevices(new BrowseQuery { FilterKeys = ["stale30"] })
            .Rows.Select(r => r.DeviceName).Should().Equal(["LT-02"]);
    }

    [Fact]
    public void BrowseDevices_EveryFilterKeyInTheCatalog_IsValidSql()
    {
        SeedDevices();

        // The catalog is the only thing that can put SQL in front of the database, so a typo in any fragment must
        // fail here rather than the first time an admin ticks that one chip.
        foreach (var filter in DeviceBrowse.Filters)
        {
            var act = () => _db.BrowseDevices(new BrowseQuery { FilterKeys = [filter.Key] });
            act.Should().NotThrow(filter.Key);
        }

        foreach (var sort in DeviceBrowse.Sorts)
        {
            var act = () => _db.BrowseDevices(new BrowseQuery { SortKey = sort.Key });
            act.Should().NotThrow(sort.Key);
        }
    }

    [Fact]
    public void EveryUserAndGroupFilter_IsValidSql()
    {
        SeedUsers();
        SeedGroups();

        foreach (var f in UserBrowse.Filters)
            ((Action)(() => _db.BrowseUsers(new BrowseQuery { FilterKeys = [f.Key] }))).Should().NotThrow(f.Key);
        foreach (var s in UserBrowse.Sorts)
            ((Action)(() => _db.BrowseUsers(new BrowseQuery { SortKey = s.Key }))).Should().NotThrow(s.Key);
        foreach (var f in GroupBrowse.Filters)
            ((Action)(() => _db.BrowseGroups(new BrowseQuery { FilterKeys = [f.Key] }))).Should().NotThrow(f.Key);
        foreach (var s in GroupBrowse.Sorts)
            ((Action)(() => _db.BrowseGroups(new BrowseQuery { SortKey = s.Key }))).Should().NotThrow(s.Key);
    }

    [Fact]
    public void EveryFilterKey_IsUnique_AndEveryFilterHasAGroup()
    {
        foreach (var catalog in new[] { UserBrowse.Filters, GroupBrowse.Filters, DeviceBrowse.Filters })
        {
            catalog.Select(f => f.Key).Should().OnlyHaveUniqueItems();
            catalog.Should().OnlyContain(f => f.Group.Length > 0, "a filter with no group cannot be OR-ed with its siblings");
        }
    }

    [Fact]
    public void Browse_OnAnEmptyCache_ReturnsAnEmptyPage_NotAnError()
    {
        _db.BrowseUsers(new BrowseQuery()).Total.Should().Be(0);
        _db.BrowseGroups(new BrowseQuery()).Total.Should().Be(0);
        _db.BrowseDevices(new BrowseQuery()).Total.Should().Be(0);
    }

    // ---- seeds -------------------------------------------------------------------------------------------------

    private void SeedUsers() => _db.Write(w =>
    {
        w.UpsertUser(new DirectoryUser
        {
            Id = "u1", UserPrincipalName = "ada@contoso.com", DisplayName = "Ada Lovelace", Mail = "ada@contoso.com",
            JobTitle = "Engineer", Department = "Engineering", AccountEnabled = true, UserType = "Member",
            OnPremisesSyncEnabled = false, Licensed = true, Raw = "{}",
        });
        w.UpsertUser(new DirectoryUser
        {
            Id = "u2", UserPrincipalName = "bea@partner.com", DisplayName = "Bea Guest", AccountEnabled = true,
            UserType = "Guest", OnPremisesSyncEnabled = false, Licensed = false, Raw = "{}",
        });
        w.UpsertUser(new DirectoryUser
        {
            Id = "u3", UserPrincipalName = "cy@contoso.com", DisplayName = "Cy Disabled", AccountEnabled = false,
            UserType = "Member", OnPremisesSyncEnabled = true, Licensed = true, Raw = "{}",
        });
        w.UpsertUser(new DirectoryUser
        {
            Id = "u4", UserPrincipalName = "dee@contoso.com", DisplayName = "Dee Unlicensed", JobTitle = "Sales",
            AccountEnabled = true, UserType = "Member", OnPremisesSyncEnabled = false, Licensed = false, Raw = "{}",
        });
    });

    private void SeedGroups() => _db.Write(w =>
    {
        w.UpsertGroup(new DirectoryGroup
        {
            Id = "g1", DisplayName = "All Staff", Kind = GroupKind.Security, IsDynamic = true,
            OnPremisesSyncEnabled = false, IsAssignableToRole = false, MemberCount = 42, Raw = "{}",
        });
        w.UpsertGroup(new DirectoryGroup
        {
            Id = "g2", DisplayName = "Old Project", Kind = GroupKind.M365, IsDynamic = false,
            OnPremisesSyncEnabled = false, IsAssignableToRole = false, MemberCount = 0, Raw = "{}",
        });
        w.UpsertGroup(new DirectoryGroup
        {
            Id = "g3", DisplayName = "Helpdesk", Kind = GroupKind.Security, IsDynamic = false,
            OnPremisesSyncEnabled = false, IsAssignableToRole = true, MemberCount = 3, Raw = "{}",
        });
    });

    private void SeedDevices() => _db.Write(w =>
    {
        w.UpsertManagedDevice(new ManagedDevice
        {
            Id = "m1", AzureAdDeviceId = "aad-1", DeviceName = "LT-01", SerialNumber = "SN-1", Model = "Latitude",
            OperatingSystem = "Windows", ComplianceState = "compliant", IsEncrypted = true,
            PrimaryUserId = "u1", PrimaryUserDisplayName = "Ada Lovelace", PrimaryUserUpn = "ada@contoso.com",
            LastSyncDateTime = DateTimeOffset.UtcNow.AddHours(-2), Raw = "{}",
        });
        w.UpsertManagedDevice(new ManagedDevice
        {
            Id = "m2", AzureAdDeviceId = "aad-2", DeviceName = "LT-02", SerialNumber = "SN-2",
            OperatingSystem = "Windows", ComplianceState = "noncompliant", IsEncrypted = false,
            LastSyncDateTime = DateTimeOffset.UtcNow.AddDays(-200), Raw = "{}",
        });
        // The Entra object for LT-01: the same device, so the list must not show it twice.
        w.UpsertDevice(new EntraDevice
        {
            Id = "e1", DeviceId = "aad-1", DisplayName = "LT-01", OperatingSystem = "Windows",
            TrustType = "AzureAd", Raw = "{}",
        });
        // Registered but never enrolled.
        w.UpsertDevice(new EntraDevice
        {
            Id = "e2", DeviceId = "aad-9", DisplayName = "REG-ONLY", OperatingSystem = "Windows",
            TrustType = "Workplace", ApproximateLastSignInDateTime = DateTimeOffset.UtcNow.AddDays(-1), Raw = "{}",
        });
    });
}
