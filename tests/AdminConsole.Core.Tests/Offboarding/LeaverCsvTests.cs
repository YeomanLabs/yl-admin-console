using System.Text;
using FluentAssertions;
using Xunit;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Search;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Offboarding;

/// <summary>
/// Offboard a list from a CSV file. The file is whatever HR's system or Excel wrote, in whatever locale; the column is
/// chosen by its name and never by position, and the values then meet the same exact-match rule as a pasted list.
/// </summary>
public sealed class LeaverCsvTests : IDisposable
{
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly SearchService _search;
    private readonly List<string> _files = [];

    public LeaverCsvTests()
    {
        _search = new SearchService(_db);
        _db.Write(w =>
        {
            w.UpsertUser(User("u-adele", "Adele Vance", "adele@contoso.com"));
            w.UpsertUser(User("u-alex-1", "Alex Wilber", "alex.wilber@contoso.com"));
            w.UpsertUser(User("u-alex-2", "Alex Wilber", "awilber@contoso.com"));
            w.UpsertUser(User("u-hans", "Hans Müller", "hans.mueller@contoso.com"));
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var file in _files) File.Delete(file);
    }

    private static DirectoryUser User(string id, string name, string upn) => DirectoryUser.FromJson(Fixtures.Parse(
        $$"""{"id":"{{id}}","displayName":"{{name}}","userPrincipalName":"{{upn}}","mail":"{{upn}}","accountEnabled":true,"assignedLicenses":[]}"""));

    private static IReadOnlyList<string> Header(string csv) => LeaverCsv.Parse(csv).Rows[0];

    private string TempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"leavers-{Guid.NewGuid():N}.csv");
        File.WriteAllBytes(path, bytes);
        _files.Add(path);
        return path;
    }

    // --- Choosing the column by its name ---

    [Fact]
    public void The_sign_in_name_column_is_chosen_over_a_display_name_wherever_it_sits()
    {
        LeaverCsv.FindColumn(Header("DisplayName,Department,UserPrincipalName\r\nAdele Vance,Sales,adele@contoso.com")).Should().Be(2);
    }

    [Theory]
    [InlineData("Name,Mail,Email", 2)]
    [InlineData("Name,DisplayName,PrimarySmtpAddress", 2)]
    [InlineData("Name,Mail,PrimarySmtpAddress", 1)]
    [InlineData("Name,DisplayName", 1)]
    [InlineData("Email,UPN", 1)]
    [InlineData("SignInName,Email", 0)]
    public void Known_columns_are_preferred_in_order(string header, int expected)
    {
        LeaverCsv.FindColumn(Header(header)).Should().Be(expected);
    }

    [Theory]
    [InlineData("Leaving date,User principal name")]
    [InlineData("Leaving date,USER_PRINCIPAL_NAME")]
    [InlineData("Leaving date,upn")]
    [InlineData("Leaving date,Sign-in name")]
    [InlineData("Leaving date,E-mail")]
    [InlineData("Leaving date,Email address")]
    [InlineData("Leaving date,Primary SMTP address")]
    public void Column_names_match_ignoring_case_spaces_and_punctuation(string header)
    {
        LeaverCsv.FindColumn(Header(header)).Should().Be(1);
    }

    [Fact]
    public void The_consoles_own_export_round_trips_on_its_sign_in_name_column()
    {
        // Users list and Reports export "Name" and "Sign-in name" (M18.5, M19).
        LeaverCsv.FindColumn(Header("Name,Sign-in name,Reason,Licences")).Should().Be(1);
    }

    [Fact]
    public void A_column_that_only_contains_a_known_name_is_never_taken_for_the_leavers()
    {
        LeaverCsv.FindColumn(Header("Manager email,Name")).Should().Be(1);
        LeaverCsv.FindColumn(Header("Manager email,Leaving date")).Should().BeNull("the admin is asked rather than handed the managers");
    }

    [Fact]
    public void A_file_with_no_known_column_name_has_no_column_chosen()
    {
        var table = LeaverCsv.Parse("Mitarbeiter;Austrittsdatum\r\nMüller, Hans;2026-10-31\r\n");

        LeaverCsv.FindColumn(table.Rows[0]).Should().BeNull();
        table.ColumnCount.Should().Be(2);
    }

    // --- RFC 4180 ---

    [Fact]
    public void Quoted_cells_keep_their_delimiters_quotes_and_line_breaks()
    {
        var table = LeaverCsv.Parse("Name,Note\r\n\"Vance, Adele\",\"said \"\"bye\"\"\r\nand left\"\r\n\"\",x\r\n");

        table.Rows.Should().HaveCount(3);
        table.Rows[1].Should().Equal("Vance, Adele", "said \"bye\"\r\nand left");
        table.Rows[2].Should().Equal("", "x");
        table.HasUnclosedQuote.Should().BeFalse();
    }

    [Fact]
    public void A_quote_inside_an_unquoted_cell_is_text()
    {
        LeaverCsv.Parse("Name\r\nO\"Brien\r\n").Rows[1].Should().Equal("O\"Brien");
    }

    [Fact]
    public void A_quote_that_is_never_closed_is_reported()
    {
        var table = LeaverCsv.Parse("Name,UPN\r\n\"Vance, Adele,adele@contoso.com\r\nMegan Bowen,megan@contoso.com\r\n");

        table.HasUnclosedQuote.Should().BeTrue();
        table.Rows.Should().HaveCount(2, "everything after the open quote is one cell");
    }

    [Fact]
    public void Line_endings_of_every_kind_and_blank_rows_are_handled()
    {
        var table = LeaverCsv.Parse("UPN,Name\nadele@contoso.com,Adele\r\n\r\n,\n,,,\rmegan@contoso.com,Megan");

        table.Rows.Select(r => r[0]).Should().Equal("UPN", "adele@contoso.com", "megan@contoso.com");
    }

    // --- Byte-order mark and encodings ---

    [Fact]
    public void A_utf8_byte_order_mark_does_not_become_part_of_the_first_column_name()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("UserPrincipalName,Name\r\nadele@contoso.com,Adele\r\n")).ToArray();

        var table = LeaverCsv.Parse(LeaverCsv.Decode(bytes));

        table.Rows[0][0].Should().Be("UserPrincipalName");
        LeaverCsv.FindColumn(table.Rows[0]).Should().Be(0);
    }

    [Fact]
    public void A_byte_order_mark_left_in_text_is_stripped_too()
    {
        LeaverCsv.Parse("﻿UPN\r\nadele@contoso.com").Rows[0].Should().Equal("UPN");
    }

    [Fact]
    public void Utf8_without_a_byte_order_mark_keeps_its_accents()
    {
        LeaverCsv.Decode(Encoding.UTF8.GetBytes("Name\r\nHans Müller")).Should().Be("Name\r\nHans Müller");
    }

    [Fact]
    public void Excels_legacy_ansi_csv_keeps_its_accents()
    {
        var windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        var bytes = windows1252.GetBytes("Name;Abteilung\r\nHans Müller;Vertrieb\r\n");

        LeaverCsv.Decode(bytes, windows1252).Should().Contain("Hans Müller");
        LeaverCsv.Decode(bytes).Should().NotContain("�", "the system code page is used when none is given");
    }

    [Fact]
    public void Utf16_with_a_byte_order_mark_is_read()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("UPN\tName\r\nadele@contoso.com\tAdele")).ToArray();

        var table = LeaverCsv.Parse(LeaverCsv.Decode(bytes));

        table.Delimiter.Should().Be('\t');
        table.Rows[1].Should().Equal("adele@contoso.com", "Adele");
    }

    // --- Delimiters ---

    [Fact]
    public void A_semicolon_file_from_european_excel_keeps_last_first_names_whole()
    {
        var table = LeaverCsv.Parse("Name;E-Mail\r\nVance, Adele;adele@contoso.com\r\n");

        table.Delimiter.Should().Be(';');
        table.Rows[1].Should().Equal("Vance, Adele", "adele@contoso.com");
    }

    [Fact]
    public void Without_a_header_a_tie_between_comma_and_semicolon_goes_to_the_semicolon()
    {
        LeaverCsv.Parse("Vance, Adele;adele@contoso.com\r\n").Rows[0].Should().Equal("Vance, Adele", "adele@contoso.com");
    }

    [Fact]
    public void A_comma_file_with_semicolons_inside_quotes_stays_a_comma_file()
    {
        var table = LeaverCsv.Parse("\"Name;Alias\",UPN\r\nAdele,adele@contoso.com");

        table.Delimiter.Should().Be(',');
        table.Rows[0].Should().Equal("Name;Alias", "UPN");
    }

    [Fact]
    public void Excels_sep_line_sets_the_delimiter_and_is_not_a_row()
    {
        var table = LeaverCsv.Parse("sep=,\r\nName;Alias,UPN\r\nAdele;A,adele@contoso.com\r\n");

        table.Delimiter.Should().Be(',');
        table.Rows[0].Should().Equal("Name;Alias", "UPN");
        table.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void Blank_lines_before_the_header_do_not_hide_the_delimiter()
    {
        var table = LeaverCsv.Parse("\r\n\r\nName;UPN\r\nAdele;adele@contoso.com\r\n");

        table.Delimiter.Should().Be(';');
        table.Rows[0].Should().Equal("Name", "UPN");
    }

    [Fact]
    public void A_file_of_blank_lines_and_empty_cells_has_no_rows()
    {
        LeaverCsv.Parse("\r\n,,\r\n,,,\r\n").Rows.Should().BeEmpty();
    }

    [Fact]
    public void A_first_row_with_no_delimiter_makes_a_single_column_file()
    {
        var table = LeaverCsv.Parse("Leavers\r\nVance, Adele\r\nmegan@contoso.com\r\n");

        table.Delimiter.Should().BeNull();
        table.ColumnCount.Should().Be(1);
        table.Rows[1].Should().Equal("Vance, Adele");
    }

    // --- Taking the column ---

    [Fact]
    public void Taking_a_column_skips_the_header_trims_and_counts_the_rows_with_nothing_in_it()
    {
        var table = LeaverCsv.Parse("Name,UPN\r\nAdele, adele@contoso.com \r\nMegan,\r\nAlex\r\nAlex W,awilber@contoso.com\r\n");

        var column = LeaverCsv.Take(table, 1, hasHeader: true);

        column.Header.Should().Be("UPN");
        column.Values.Should().Equal("adele@contoso.com", "awilber@contoso.com");
        column.EmptyRows.Should().Be(2, "one cell is empty and one row is short");
    }

    [Fact]
    public void Without_a_header_the_first_row_is_read_as_a_person()
    {
        var table = LeaverCsv.Parse("adele@contoso.com,Sales\r\nmegan@contoso.com,Finance\r\n");

        var column = LeaverCsv.Take(table, 0, hasHeader: false);

        column.Header.Should().BeNull();
        column.Values.Should().Equal("adele@contoso.com", "megan@contoso.com");
    }

    // --- Reading the file ---

    [Fact]
    public void A_file_still_open_in_excel_can_be_read()
    {
        var path = TempFile(Encoding.UTF8.GetBytes("UPN\r\nadele@contoso.com\r\n"));
        using var excel = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        LeaverCsv.ReadFile(path).Should().Be("UPN\r\nadele@contoso.com\r\n");
    }

    [Fact]
    public void A_file_far_too_large_to_be_a_leaver_list_is_refused()
    {
        var path = TempFile([]);
        using (var stream = File.OpenWrite(path)) stream.SetLength(LeaverCsv.MaxFileBytes + 1);

        var read = () => LeaverCsv.ReadFile(path);

        read.Should().Throw<InvalidDataException>().WithMessage("*right file*");
    }

    // --- Through to the list's exact-match rule ---

    [Fact]
    public void Imported_values_resolve_with_the_same_exact_match_rule_as_a_pasted_list()
    {
        var windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        var bytes = windows1252.GetBytes("Mitarbeiter;Name;Abteilung\r\n1;Vance, Adele;Vertrieb\r\n2;Alex Wilber;Einkauf\r\n3;Hans Müller;IT\r\n4;Megan;IT\r\n5;Adele Vance;Vertrieb\r\n");
        var table = LeaverCsv.Parse(LeaverCsv.Decode(bytes, windows1252));
        var column = LeaverCsv.Take(table, LeaverCsv.FindColumn(table.Rows[0])!.Value, hasHeader: true);

        // The dialog puts the values in the list box, one per line, and finds them as if they were pasted.
        var entries = OffboardList.Resolve(OffboardList.Parse(string.Join("\r\n", column.Values)), _db, _search);

        entries.Select(e => e.Match).Should().Equal(ListMatch.Found, ListMatch.Ambiguous, ListMatch.Found, ListMatch.NotFound, ListMatch.Duplicate);
        entries[0].User!.Id.Should().Be("u-adele");
        entries[1].User.Should().BeNull("a shared name is never picked");
        entries[2].User!.Id.Should().Be("u-hans");
        entries[3].User.Should().BeNull("a near miss is never enough");
    }
}
