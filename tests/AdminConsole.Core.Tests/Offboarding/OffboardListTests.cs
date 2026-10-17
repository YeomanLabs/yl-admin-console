using FluentAssertions;
using Xunit;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Search;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Offboarding;

/// <summary>
/// M15.5 — offboard a list. The scripted chain this replaces matched names with a wildcard, which is how a list of
/// leavers disables someone who still works there. Every test here is about refusing to guess.
/// </summary>
public sealed class OffboardListTests : IDisposable
{
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly SearchService _search;

    public OffboardListTests()
    {
        _search = new SearchService(_db);
        _db.Write(w =>
        {
            w.UpsertUser(User("u-adele", "Adele Vance", "adele@contoso.com"));
            w.UpsertUser(User("u-alex-1", "Alex Wilber", "alex.wilber@contoso.com", "Sales"));
            w.UpsertUser(User("u-alex-2", "Alex Wilber", "awilber@contoso.com", "Finance"));
            w.UpsertUser(User("u-megan", "Megan Bowen", "megan@contoso.com"));
        });
    }

    public void Dispose() => _db.Dispose();

    private static DirectoryUser User(string id, string name, string upn, string? department = null) => DirectoryUser.FromJson(Fixtures.Parse(
        $$"""{"id":"{{id}}","displayName":"{{name}}","userPrincipalName":"{{upn}}","mail":"{{upn}}","department":{{(department is null ? "null" : $"\"{department}\"")}},"accountEnabled":true,"assignedLicenses":[]}"""));

    private IReadOnlyList<ListEntry> Resolve(string text) => OffboardList.Resolve(OffboardList.Parse(text), _db, _search);

    [Fact]
    public void A_pasted_list_splits_by_line_and_semicolon_but_keeps_a_last_first_name_whole()
    {
        OffboardList.Parse("adele@contoso.com\r\nVance, Adele; megan@contoso.com, alex.wilber@contoso.com\n\n")
            .Should().Equal("adele@contoso.com", "Vance, Adele", "megan@contoso.com", "alex.wilber@contoso.com");
    }

    [Fact]
    public void Addresses_copied_from_a_mail_client_are_cleaned()
    {
        OffboardList.Parse("\"Adele Vance\" <adele@contoso.com>, mailto:megan@contoso.com")
            .Should().Equal("adele@contoso.com", "megan@contoso.com");
    }

    [Fact]
    public void A_sign_in_name_or_a_unique_name_resolves_to_exactly_one_person()
    {
        var entries = Resolve("ADELE@contoso.com\nMegan Bowen\nVance, Adele");

        entries[0].Match.Should().Be(ListMatch.Found);
        entries[0].User!.Id.Should().Be("u-adele");
        entries[1].User!.Id.Should().Be("u-megan");
        entries[2].Match.Should().Be(ListMatch.Duplicate, "the same person twice is offboarded once");
    }

    [Fact]
    public void A_shared_name_is_ambiguous_and_nobody_is_picked()
    {
        var entry = Resolve("Alex Wilber").Single();

        entry.Match.Should().Be(ListMatch.Ambiguous);
        entry.User.Should().BeNull();
        entry.Candidates.Select(c => c.Id).Should().BeEquivalentTo(["u-alex-1", "u-alex-2"]);
    }

    [Fact]
    public void A_near_miss_is_not_found_and_close_matches_are_offered_not_chosen()
    {
        var entry = Resolve("Megan").Single();

        entry.Match.Should().Be(ListMatch.NotFound);
        entry.User.Should().BeNull("a prefix is never enough to offboard someone");
        entry.Candidates.Select(c => c.Id).Should().Contain("u-megan");
    }
}
