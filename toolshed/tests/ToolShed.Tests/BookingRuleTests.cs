using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

public class BookingRuleTests
{
    private static readonly DateOnly Today = new(2026, 5, 1);

    private static string? Validate(string start, string end, int maxLoanDays = 14) =>
        BookingService.ValidateRange(new DateRange(DateOnly.Parse(start), DateOnly.Parse(end)), Today, maxLoanDays);

    [Fact]
    public void A_normal_window_is_accepted()
    {
        Assert.Null(Validate("2026-05-04", "2026-05-07"));
    }

    [Fact]
    public void Same_day_loans_are_accepted()
    {
        Assert.Null(Validate("2026-05-01", "2026-05-01"));
    }

    [Fact]
    public void Return_date_before_collection_is_rejected()
    {
        Assert.NotNull(Validate("2026-05-07", "2026-05-04"));
    }

    [Fact]
    public void Loans_cannot_start_in_the_past()
    {
        Assert.NotNull(Validate("2026-04-30", "2026-05-02"));
    }

    [Fact]
    public void Loans_longer_than_the_owners_limit_are_rejected()
    {
        Assert.Null(Validate("2026-05-01", "2026-05-03", maxLoanDays: 3));
        Assert.NotNull(Validate("2026-05-01", "2026-05-04", maxLoanDays: 3));
    }

    [Fact]
    public void Loans_too_far_ahead_are_rejected()
    {
        var justInside = Today.AddDays(BookingService.MaxDaysAhead);
        var tooFar = Today.AddDays(BookingService.MaxDaysAhead + 1);

        Assert.Null(BookingService.ValidateRange(new DateRange(justInside, justInside), Today, 14));
        Assert.NotNull(BookingService.ValidateRange(new DateRange(tooFar, tooFar), Today, 14));
    }
}
