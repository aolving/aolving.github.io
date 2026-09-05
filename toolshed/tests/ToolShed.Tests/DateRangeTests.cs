using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

public class DateRangeTests
{
    private static DateRange Range(string start, string end) =>
        new(DateOnly.Parse(start), DateOnly.Parse(end));

    [Theory]
    // Identical windows.
    [InlineData("2026-05-01", "2026-05-03", "2026-05-01", "2026-05-03", true)]
    // One inside the other.
    [InlineData("2026-05-01", "2026-05-10", "2026-05-04", "2026-05-05", true)]
    // Touching at a single day: an inclusive range means this is still a clash.
    [InlineData("2026-05-01", "2026-05-03", "2026-05-03", "2026-05-06", true)]
    // Back to back, no shared day.
    [InlineData("2026-05-01", "2026-05-03", "2026-05-04", "2026-05-06", false)]
    // Far apart.
    [InlineData("2026-05-01", "2026-05-03", "2026-07-01", "2026-07-03", false)]
    public void Overlaps_matches_inclusive_day_semantics(string aStart, string aEnd, string bStart, string bEnd, bool expected)
    {
        var a = Range(aStart, aEnd);
        var b = Range(bStart, bEnd);

        Assert.Equal(expected, a.Overlaps(b));
        Assert.Equal(expected, b.Overlaps(a));
    }

    [Fact]
    public void Days_counts_both_end_points()
    {
        Assert.Equal(1, Range("2026-05-01", "2026-05-01").Days);
        Assert.Equal(3, Range("2026-05-01", "2026-05-03").Days);
    }

    [Fact]
    public void Backwards_range_is_not_well_formed()
    {
        Assert.False(Range("2026-05-05", "2026-05-01").IsWellFormed);
        Assert.True(Range("2026-05-01", "2026-05-05").IsWellFormed);
    }
}
