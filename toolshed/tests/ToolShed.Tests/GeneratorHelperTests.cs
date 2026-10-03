using ToolShed.Web.Pages.Admin;
using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

public class GeneratorHelperTests
{
    // ---------------------------------------------------------------- CSV

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has \"quote\"", "\"has \"\"quote\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void Fields_are_quoted_when_they_need_to_be(string? value, string expected)
    {
        Assert.Equal(expected, CsvWriter.Escape(value));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=1")]
    public void Spreadsheet_formulas_are_defused_by_a_leading_apostrophe(string value)
    {
        var escaped = CsvWriter.Escape(value);

        Assert.StartsWith("'", escaped.TrimStart('"'));
    }

    [Fact]
    public void A_row_joins_escaped_fields_and_the_file_has_a_byte_order_mark_for_excel()
    {
        Assert.Equal("123 456,a@b.test,\"x,y\"", CsvWriter.Row("123 456", "a@b.test", "x,y"));

        var bytes = CsvWriter.ToBytes(["h1,h2", "a,b"]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("h1,h2\r\na,b\r\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }

    // ---------------------------------------------------------------- email list

    [Fact]
    public void A_pasted_list_is_split_on_lines_commas_and_semicolons()
    {
        var emails = InvitationsModel.ParseEmails("a@example.test\r\nb@example.test, c@example.test; d@example.test\n\n", out var invalid);

        Assert.Equal(new[] { "a@example.test", "b@example.test", "c@example.test", "d@example.test" }, emails.ToArray());
        Assert.Empty(invalid);
    }

    [Fact]
    public void Repeats_are_dropped_without_regard_to_case()
    {
        var emails = InvitationsModel.ParseEmails("a@example.test\nA@Example.Test\n a@example.test ", out _);

        Assert.Equal(new[] { "a@example.test" }, emails.ToArray());
    }

    [Fact]
    public void Things_that_are_not_addresses_are_reported_not_silently_dropped()
    {
        var emails = InvitationsModel.ParseEmails("good@example.test\nnot an address\nalso-bad\nok@example.test", out var invalid);

        Assert.Equal(new[] { "good@example.test", "ok@example.test" }, emails.ToArray());
        Assert.Equal(2, invalid.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n \r\n ")]
    public void An_empty_list_is_simply_empty(string? text)
    {
        Assert.Empty(InvitationsModel.ParseEmails(text, out var invalid));
        Assert.Empty(invalid);
    }
}
