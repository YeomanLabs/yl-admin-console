using FluentAssertions;
using Xunit;
using AdminConsole.Core.Export;

namespace AdminConsole.Core.Tests.Export;

/// <summary>M18.5: the CSV an export writes.</summary>
public sealed class CsvExportTests
{
    [Theory]
    [InlineData("Ada", "Ada")]
    [InlineData("Lovelace, Ada", "\"Lovelace, Ada\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Cell_QuotesOnlyWhatNeedsIt(string? value, string expected) => CsvExport.Cell(value).Should().Be(expected);

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\",\"click\")")]
    [InlineData("+1 555 0100")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void Cell_AFormulaStart_IsDefusedSoExcelReadsText(string value)
    {
        // A display name is whatever someone typed; opening the export must never run it.
        var cell = CsvExport.Cell(value);
        cell.TrimStart('"').Should().StartWith("'");
    }

    [Fact]
    public void Write_HeadersThenRows_WithCrLf()
    {
        using var writer = new StringWriter();
        CsvExport.Write(writer, ["Name", "Dept"], [["Ada", "Eng"], ["Bea", null]]);

        writer.ToString().Should().Be("Name,Dept\r\nAda,Eng\r\nBea,\r\n");
    }

    [Fact]
    public void Utf8WithBom_EmitsTheMarkExcelNeedsForAccents() =>
        CsvExport.Utf8WithBom.GetPreamble().Should().Equal(0xEF, 0xBB, 0xBF);
}
