using ToolShed.Client;
using Xunit;

namespace ToolShed.Tests;

public class DateSelectionTests
{
    private static readonly DateOnly Today = new(2026, 5, 1);

    private static DateOnly D(int offset) => Today.AddDays(offset);

    private static DateSelection New(int maxLoanDays = 14, params (int Start, int End)[] held) =>
        new(Today, maxLoanDays, held.Select(h => (D(h.Start), D(h.End))));

    // ---------------------------------------------------------------- the first date is the start

    [Fact]
    public void Nothing_is_chosen_to_begin_with()
    {
        var s = New();

        Assert.Null(s.Start);
        Assert.Null(s.End);
        Assert.False(s.IsComplete);
        Assert.Equal(0, s.Days);
        Assert.Contains("Pick the first day", s.Summary());
    }

    [Fact]
    public void The_first_day_picked_is_always_the_start()
    {
        var s = New();

        Assert.True(s.Pick(D(5)));

        Assert.Equal(D(5), s.Start);
        Assert.Null(s.End);
    }

    [Fact]
    public void The_second_day_picked_is_the_end()
    {
        var s = New();
        s.Pick(D(5));

        Assert.True(s.Pick(D(7)));

        Assert.Equal(D(5), s.Start);
        Assert.Equal(D(7), s.End);
        Assert.True(s.IsComplete);
        Assert.Equal(3, s.Days);
    }

    // ---------------------------------------------------------------- no going backwards

    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    [InlineData(0)]
    public void Days_before_the_start_cannot_be_picked_as_the_end(int earlier)
    {
        var s = New();
        s.Pick(D(5));

        Assert.False(s.CanPick(D(earlier)));
        Assert.False(s.Pick(D(earlier)));

        // Nothing moved: still waiting for an end, and the start has not been swapped.
        Assert.Equal(D(5), s.Start);
        Assert.Null(s.End);
    }

    [Fact]
    public void The_end_can_never_be_before_the_start_however_the_clicks_come()
    {
        var s = New();
        var rng = new Random(7);

        for (var i = 0; i < 2000; i++)
        {
            s.Pick(D(rng.Next(-5, 200)));

            if (s.IsComplete)
            {
                Assert.True(s.End >= s.Start, $"end {s.End} before start {s.Start}");
                Assert.True(s.Days >= 1);
            }
        }
    }

    [Fact]
    public void A_booking_is_never_shorter_than_one_day()
    {
        var s = New();
        s.Pick(D(5));
        s.Pick(D(5));

        Assert.Equal(1, s.Days);
        Assert.Equal("Wed 6 May (1 day)", s.Summary());
    }

    // ---------------------------------------------------------------- same day is fine

    [Fact]
    public void A_single_day_booking_is_allowed_by_picking_the_same_day_twice()
    {
        var s = New();
        s.Pick(D(5));

        Assert.True(s.CanPick(D(5)));
        Assert.True(s.Pick(D(5)));

        Assert.Equal(D(5), s.Start);
        Assert.Equal(D(5), s.End);
    }

    // ---------------------------------------------------------------- restarting

    [Fact]
    public void Picking_again_after_a_finished_selection_starts_a_new_one()
    {
        var s = New();
        s.Pick(D(5));
        s.Pick(D(7));

        Assert.True(s.Pick(D(2)));

        Assert.Equal(D(2), s.Start);
        Assert.Null(s.End);
    }

    [Fact]
    public void Clearing_forgets_both_dates()
    {
        var s = New();
        s.Pick(D(5));
        s.Pick(D(7));

        s.Clear();

        Assert.Null(s.Start);
        Assert.Null(s.End);
        Assert.True(s.CanPick(D(3)));
    }

    // ---------------------------------------------------------------- the booking window

    [Fact]
    public void Past_days_cannot_be_picked()
    {
        var s = New();

        Assert.False(s.CanPick(D(-1)));
        Assert.False(s.Pick(D(-10)));
        Assert.True(s.CanPick(D(0)));
    }

    [Fact]
    public void Days_beyond_the_booking_horizon_cannot_be_picked()
    {
        var s = New();

        Assert.True(s.CanPick(D(DateSelection.HorizonDays)));
        Assert.False(s.CanPick(D(DateSelection.HorizonDays + 1)));
    }

    // ---------------------------------------------------------------- the owner's limit

    [Fact]
    public void The_end_cannot_be_further_than_the_owners_longest_loan()
    {
        var s = New(maxLoanDays: 3);
        s.Pick(D(10));

        Assert.True(s.CanPick(D(12)));      // three days, counting both ends
        Assert.False(s.CanPick(D(13)));     // four
        Assert.Equal(D(12), s.LatestEnd());
    }

    [Fact]
    public void A_one_day_limit_allows_only_the_start_day_as_the_end()
    {
        var s = New(maxLoanDays: 1);
        s.Pick(D(4));

        Assert.True(s.CanPick(D(4)));
        Assert.False(s.CanPick(D(5)));
    }

    // ---------------------------------------------------------------- other people's bookings

    [Fact]
    public void Days_already_requested_or_booked_cannot_be_picked_as_the_start()
    {
        var s = New(14, (5, 7));

        Assert.False(s.CanPick(D(5)));
        Assert.False(s.CanPick(D(6)));
        Assert.False(s.CanPick(D(7)));
        Assert.True(s.CanPick(D(4)));
        Assert.True(s.CanPick(D(8)));
    }

    [Fact]
    public void The_end_cannot_run_across_somebody_elses_booking()
    {
        var s = New(14, (8, 10));
        s.Pick(D(5));

        Assert.True(s.CanPick(D(7)));       // right up to the day before it
        Assert.False(s.CanPick(D(8)));
        Assert.False(s.CanPick(D(11)));     // nor leaping over it
        Assert.Equal(D(7), s.LatestEnd());
    }

    [Fact]
    public void A_start_right_before_a_booking_can_only_be_a_single_day()
    {
        var s = New(14, (6, 9));
        s.Pick(D(5));

        Assert.True(s.CanPick(D(5)));
        Assert.False(s.CanPick(D(6)));
        Assert.Equal(D(5), s.LatestEnd());
    }

    [Fact]
    public void Only_the_next_booking_after_the_start_limits_the_end()
    {
        var s = New(30, (3, 4), (12, 14), (20, 22));
        s.Pick(D(6));

        Assert.Equal(D(11), s.LatestEnd());
        Assert.False(s.CanPick(D(13)));
        Assert.False(s.CanPick(D(21)));
    }

    [Fact]
    public void A_booking_before_the_start_does_not_limit_the_end()
    {
        var s = New(5, (1, 3));
        s.Pick(D(5));

        Assert.Equal(D(9), s.LatestEnd());
    }

    [Fact]
    public void Held_ranges_may_be_given_in_any_order()
    {
        var s = New(30, (20, 22), (8, 10));
        s.Pick(D(5));

        Assert.Equal(D(7), s.LatestEnd());
    }

    [Fact]
    public void A_picked_selection_never_overlaps_a_held_range()
    {
        var s = New(10, (8, 10), (15, 16));
        var rng = new Random(11);

        for (var i = 0; i < 3000; i++)
        {
            s.Pick(D(rng.Next(0, 40)));
            if (!s.IsComplete)
            {
                continue;
            }

            Assert.False(Enumerable.Range(0, s.Days).Any(n => s.IsHeld(s.Start!.Value.AddDays(n))),
                $"{s.Start} to {s.End} overlaps a held range");
            Assert.True(s.Days <= 10);
        }
    }

    // ---------------------------------------------------------------- the prompt

    [Fact]
    public void The_prompt_tells_you_what_to_do_next()
    {
        var s = New();
        s.Pick(D(5));
        Assert.Contains("Now pick the last day", s.Summary());
        Assert.Contains("Earlier days cannot be chosen", s.Summary());

        s.Pick(D(8));
        Assert.Equal("Wed 6 May to Sat 9 May (4 days)", s.Summary());
    }

    // ---------------------------------------------------------------- agreement with the server

    [Fact]
    public void Whatever_the_calendar_lets_you_pick_the_server_accepts()
    {
        var held = new[] { (D(8), D(10)), (D(15), D(16)) };
        var s = New(6, (8, 10), (15, 16));
        var rng = new Random(3);

        for (var i = 0; i < 3000; i++)
        {
            s.Pick(D(rng.Next(0, 60)));
            if (!s.IsComplete)
            {
                continue;
            }

            var range = new ToolShed.Web.Services.DateRange(s.Start!.Value, s.End!.Value);
            Assert.Null(ToolShed.Web.Services.BookingService.ValidateRange(range, Today, 6));
            Assert.DoesNotContain(held, h => range.Overlaps(new ToolShed.Web.Services.DateRange(h.Item1, h.Item2)));
        }
    }
}
