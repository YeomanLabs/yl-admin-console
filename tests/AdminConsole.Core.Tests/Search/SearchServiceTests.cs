using System.Diagnostics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.Search;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Search;

public sealed class SearchServiceTests : IDisposable
{
    private static readonly string[] FirstNames =
    [
        "Ada", "Grace", "Linus", "Margaret", "Alan", "Barbara", "Dennis", "Ken", "Radia", "Tim", "Vint", "Donald", "Edsger", "Frances",
        "Guido", "Hedy", "James", "Katherine", "Leslie", "Mary", "Niklaus", "Ole", "Peter", "Rasmus", "Sophie", "Tony", "Ursula", "Vera",
        "Walter", "Xavier", "Yukihiro", "Zed", "Nadia", "Sam", "Jose", "Anders", "Bjarne", "Brendan", "Carol", "Dana",
    ];

    private static readonly string[] LastNames =
    [
        "Lovelace", "Hopper", "Torvalds", "Hamilton", "Turing", "Liskov", "Ritchie", "Thompson", "Perlman", "Berners-Lee", "Cerf", "Knuth",
        "Dijkstra", "Allen", "Rossum", "Lamarr", "Gosling", "Johnson", "Lamport", "Shaw", "Wirth", "Dahl", "Norvig", "Lerdorf", "Wilson",
        "Hoare", "Franklin", "Rubin", "Pitts", "Niven", "Matsumoto", "Smith", "Jones", "Jonas", "Garcia", "Hejlsberg", "Stroustrup", "Eich",
        "Adams", "Baker", "Clark", "Davis", "Evans", "Foster", "Green", "Hall", "Irwin", "King", "Lewis", "Moore",
    ];

    private static readonly string[] GroupWords = ["Sales", "Engineering", "Finance", "Helpdesk", "Intune", "Autopilot", "VPN", "Printers", "Marketing", "Legal"];

    private readonly ITestOutputHelper _output;
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly FakeTimeProvider _time = new();
    private readonly SearchService _sut;

    public SearchServiceTests(ITestOutputHelper output)
    {
        _output = output;
        Seed(_db, users: 5000, devices: 2000, groups: 1000);
        _sut = new SearchService(_db,
        [
            new SearchAction { Id = "offboard", Title = "Offboard user", Aliases = ["decom", "leaver"], Description = "Run the offboarding checklist" },
            new SearchAction { Id = "new-user", Title = "New user from template", Aliases = ["create user"] },
            new SearchAction { Id = "sync", Title = "Sync now", Aliases = ["refresh"] },
        ], _time);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void P95_Under100ms_On5000Users2000Devices1000Groups()
    {
        string[] queries =
        [
            "ada", "ada lov", "grace", "smi", "u:jo", "d:desk", "g:sales", "sn00", "0042", "test", "lovelace", "hop", "ada.lovelace",
            "desktop-00", "engineering", "vpn", ">off", "e1", "linus tor", "mar", "ham", "5cd", "jose", "sam j", "anders hej", "ada@", "z",
            "de", "helpdesk", "printers", "34xyz", "2065550199", "206 555", "u:ada", "d:sn", "g:legal", "ken tho", "wilson", "foster", "king",
        ];

        foreach (var q in queries.Take(5)) _sut.Search(q, SearchScope.All, 8, CancellationToken.None);

        var timings = new List<double>();
        foreach (var q in queries.Concat(queries).Concat(queries))
        {
            var sw = Stopwatch.StartNew();
            _sut.Search(q, SearchScope.All, 8, CancellationToken.None);
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var p50 = timings[timings.Count / 2];
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        var max = timings[^1];
        _output.WriteLine($"palette search over {timings.Count} queries: p50 {p50:F1} ms, p95 {p95:F1} ms, max {max:F1} ms");

        p95.Should().BeLessThan(100, "doc 01 success criterion: palette query → results < 100 ms at 5,000 users");
    }

    [Fact]
    public void Ranking_PrefixOnDisplayName_BeatsUpnPrefix_BeatsWordPrefix()
    {
        _db.Write(w =>
        {
            w.UpsertUser(User("r1", "Xylo Prime", "xylo.prime@contoso.com"));
            w.UpsertUser(User("r2", "Zed Smith", "xylo.z@contoso.com"));
            w.UpsertUser(User("r3", "Grace Xylophone", "grace.x@contoso.com"));
        });

        var hits = _sut.Search("xylo", SearchScope.Users, 8, CancellationToken.None).Users;

        hits.Select(h => h.Id).Take(3).Should().Equal("r1", "r2", "r3");
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
        hits[1].Score.Should().BeGreaterThan(hits[2].Score);
    }

    [Fact]
    public void Ranking_RecentlyOpenedObject_GetsBoosted()
    {
        _db.Write(w =>
        {
            w.UpsertUser(User("s1", "Sam Jones", "sam.jones@contoso.com"));
            w.UpsertUser(User("s2", "Sam Jonas", "sam.jonas@contoso.com"));
        });

        var before = _sut.Search("sam jon", SearchScope.Users, 8, CancellationToken.None).Users;
        // Same tier → alphabetical tiebreak.
        before.Select(h => h.Id).Take(2).Should().Equal("s2", "s1");

        _db.TouchRecent("s1", "user", _time.GetUtcNow());
        var after = _sut.Search("sam jon", SearchScope.Users, 8, CancellationToken.None).Users;
        after[0].Id.Should().Be("s1");

        _time.Advance(TimeSpan.FromDays(8));
        _sut.Search("sam jon", SearchScope.Users, 8, CancellationToken.None).Users[0].Id.Should().Be("s2", "the boost expires after 7 days");
    }

    [Fact]
    public void Prefixes_NarrowScope()
    {
        _db.Write(w => w.UpsertGroup(new DirectoryGroup { Id = "gs", DisplayName = "Sales", Kind = GroupKind.M365, IsDynamic = true, OnPremisesSyncEnabled = false, IsAssignableToRole = false, MemberCount = 12, Raw = "{}" }));
        _db.Write(w => w.UpsertUser(User("us", "Sales Bot", "sales@contoso.com")));

        var all = _sut.Search("sales", SearchScope.All, 8, CancellationToken.None);
        all.Users.Should().NotBeEmpty();
        all.Groups.Should().NotBeEmpty();

        var users = _sut.Search("u:sales", SearchScope.All, 8, CancellationToken.None);
        users.Query.Scope.Should().Be(SearchScope.Users);
        users.Users.Should().NotBeEmpty();
        users.Groups.Should().BeEmpty();

        var groups = _sut.Search("g:sales", SearchScope.All, 8, CancellationToken.None);
        groups.Groups.Should().Contain(h => h.Id == "gs").Which.Should().BeEquivalentTo(new { Secondary = "12 members", Badges = new[] { "M365", "Dynamic" } });
        groups.Users.Should().BeEmpty();

        var actions = _sut.Search(">off", SearchScope.All, 8, CancellationToken.None);
        actions.Actions.Should().ContainSingle().Which.Id.Should().Be("offboard");
        actions.Users.Should().BeEmpty();

        _sut.Search(">", SearchScope.All, 8, CancellationToken.None).Actions.Should().HaveCount(3, "a bare > lists every action");
        _sut.Search("decom", SearchScope.All, 8, CancellationToken.None).Actions.Should().ContainSingle().Which.Id.Should().Be("offboard");
        _sut.Search("sync now", SearchScope.Actions, 8, CancellationToken.None).Actions.Should().ContainSingle().Which.Id.Should().Be("sync");
    }

    [Fact]
    public void PhoneDigits_MatchByPrefix_AcrossFormatting()
    {
        _db.Write(w => w.UpsertUser(User("p1", "Phone Person", "phone@contoso.com") with { PhonesDigits = "12065550199", EmployeeId = "E77" }));

        _sut.Search("2065550199", SearchScope.All, 8, CancellationToken.None).Users.Should().BeEmpty("digits are matched as a prefix of the stored digits, which start with the country code");
        _sut.Search("1206 555", SearchScope.All, 8, CancellationToken.None).Users.Should().ContainSingle().Which.Id.Should().Be("p1");
        _sut.Search("+1 (206) 555-0199", SearchScope.All, 8, CancellationToken.None).Users.Should().ContainSingle().Which.Id.Should().Be("p1");
        _sut.Search("e77", SearchScope.Users, 8, CancellationToken.None).Users.Should().Contain(h => h.Id == "p1");
    }

    [Fact]
    public void DeviceSerial_MatchesByPrefixAndBySuffix()
    {
        _db.Write(w => w.UpsertManagedDevice(new ManagedDevice { Id = "dx", DeviceName = "LT-FIELD-01", SerialNumber = "5CD1234XYZ", Model = "EliteBook 840", ComplianceState = "noncompliant", IsEncrypted = false, Raw = "{}" }));

        var bySuffix = _sut.Search("34xyz", SearchScope.All, 8, CancellationToken.None);
        bySuffix.Devices.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = "dx", Primary = "LT-FIELD-01", Secondary = "5CD1234XYZ · EliteBook 840", Badges = new[] { "Non-compliant", "Not encrypted" } });

        _sut.Search("5cd12", SearchScope.Devices, 8, CancellationToken.None).Devices.Should().Contain(h => h.Id == "dx");
        _sut.Search("d:lt-field", SearchScope.All, 8, CancellationToken.None).Devices.Should().Contain(h => h.Id == "dx");
        _sut.Search("elitebook", SearchScope.All, 8, CancellationToken.None).Devices.Should().Contain(h => h.Id == "dx");
    }

    [Fact]
    public void Diacritics_AreFolded_AndBadgesRender()
    {
        _db.Write(w => w.UpsertUser(User("j1", "José García", "jose.garcia@contoso.com") with { AccountEnabled = false, OnPremisesSyncEnabled = true, UserType = "Guest" }));

        var hit = _sut.Search("jose gar", SearchScope.Users, 8, CancellationToken.None).Users.Should().Contain(h => h.Id == "j1").Which;
        hit.Badges.Should().Equal("Disabled", "Synced", "Guest");
        hit.Secondary.Should().Be("jose.garcia@contoso.com");
    }

    [Fact]
    public void EmptyQuery_ReturnsNothingQuickly()
    {
        var r = _sut.Search("   ", SearchScope.All, 8, CancellationToken.None);
        r.IsEmpty.Should().BeTrue();
        r.Query.IsEmpty.Should().BeTrue();
        r.Top.Should().BeNull();
    }

    [Fact]
    public void LimitPerGroup_IsHonored_AndTopIsHighestScore()
    {
        var r = _sut.Search("a", SearchScope.All, 3, CancellationToken.None);
        r.Users.Should().HaveCount(3);
        r.Devices.Count.Should().BeLessThanOrEqualTo(3);
        r.Groups.Count.Should().BeLessThanOrEqualTo(3);
        r.Top!.Score.Should().Be(r.All.Max(h => h.Score));
    }

    [Theory]
    [InlineData("u: Ada", SearchScope.All, SearchScope.Users, "ada")]
    [InlineData("D:abc", SearchScope.All, SearchScope.Devices, "abc")]
    [InlineData("g:", SearchScope.All, SearchScope.Groups, "")]
    [InlineData(">offboard", SearchScope.Users, SearchScope.Actions, "offboard")]
    [InlineData("plain", SearchScope.Groups, SearchScope.Groups, "plain")]
    public void Parse_AppliesPrefixes(string input, SearchScope given, SearchScope expected, string text)
    {
        var p = SearchService.Parse(input, given);
        p.Scope.Should().Be(expected);
        p.Text.Should().Be(text);
    }

    [Fact]
    public void Parse_TokenizesLikeFts_AndDetectsDigits()
    {
        SearchService.Parse("Ada@Contoso.com", SearchScope.All).Terms.Should().Equal("ada", "contoso", "com");
        SearchService.Parse("+1 (425) 555-0100", SearchScope.All).Digits.Should().Be("14255550100");
        SearchService.Parse("e1001", SearchScope.All).Digits.Should().BeNull("letters mean it is not a phone fragment");
        SearchService.Parse("42", SearchScope.All).Digits.Should().BeNull("fewer than 3 digits");
        SearchService.BuildMatch(["ada", "lov"]).Should().Be("\"ada\"* \"lov\"*");
        SearchService.BuildMatch(["1206"], "phones_digits").Should().Be("phones_digits: (\"1206\"*)");
    }

    // ---- seed ------------------------------------------------------------------------------------------------------

    private static void Seed(CacheDb db, int users, int devices, int groups)
    {
        db.Write(w =>
        {
            for (var i = 0; i < users; i++)
            {
                var first = FirstNames[i % FirstNames.Length];
                var last = LastNames[(i / FirstNames.Length) % LastNames.Length];
                var upn = $"{first}.{last}{i}@contoso.com".ToLowerInvariant().Replace("-", string.Empty);
                w.UpsertUser(new DirectoryUser
                {
                    Id = $"u{i:D5}",
                    UserPrincipalName = upn,
                    DisplayName = $"{first} {last}",
                    Mail = upn,
                    GivenName = first,
                    Surname = last,
                    JobTitle = i % 7 == 0 ? "Engineer" : i % 7 == 1 ? "Analyst" : null,
                    Department = GroupWords[i % GroupWords.Length],
                    EmployeeId = $"E{10000 + i}",
                    PhonesDigits = i % 3 == 0 ? $"1425555{i % 10000:D4}" : null,
                    AccountEnabled = i % 23 != 0,
                    UserType = i % 41 == 0 ? "Guest" : "Member",
                    OnPremisesSyncEnabled = i % 5 == 0,
                    Licensed = i % 4 != 0,
                    Raw = "{}",
                });
            }

            for (var i = 0; i < devices; i++)
            {
                var owner = i % users;
                w.UpsertManagedDevice(new ManagedDevice
                {
                    Id = $"m{i:D5}",
                    DeviceName = i % 2 == 0 ? $"DESKTOP-{i:D5}" : $"LT-{LastNames[i % LastNames.Length].ToUpperInvariant()}-{i}",
                    SerialNumber = $"SN{i:D7}",
                    Model = i % 3 == 0 ? "Surface Laptop 5" : i % 3 == 1 ? "Latitude 5540" : "EliteBook 840",
                    Manufacturer = "Contoso",
                    ComplianceState = i % 9 == 0 ? "noncompliant" : "compliant",
                    IsEncrypted = i % 13 != 0,
                    PrimaryUserId = $"u{owner:D5}",
                    PrimaryUserUpn = $"user{owner}@contoso.com",
                    PrimaryUserDisplayName = $"{FirstNames[owner % FirstNames.Length]} {LastNames[(owner / FirstNames.Length) % LastNames.Length]}",
                    Raw = "{}",
                });
            }

            for (var i = 0; i < groups; i++)
            {
                w.UpsertGroup(new DirectoryGroup
                {
                    Id = $"g{i:D5}",
                    DisplayName = $"{GroupWords[i % GroupWords.Length]} {LastNames[i % LastNames.Length]} {i}",
                    Description = i % 4 == 0 ? "Scope for device configuration" : null,
                    Mail = i % 2 == 0 ? $"group{i}@contoso.com" : null,
                    Kind = i % 3 == 0 ? GroupKind.M365 : GroupKind.Security,
                    IsDynamic = i % 10 == 0,
                    OnPremisesSyncEnabled = i % 7 == 0,
                    IsAssignableToRole = false,
                    MemberCount = i % 50,
                    Raw = "{}",
                });
            }
        });
    }

    private static DirectoryUser User(string id, string name, string upn) => new()
    {
        Id = id,
        UserPrincipalName = upn,
        DisplayName = name,
        Mail = upn,
        AccountEnabled = true,
        OnPremisesSyncEnabled = false,
        Licensed = true,
        Raw = "{}",
    };
}
