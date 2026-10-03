using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Models;
using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

public class BookingServiceTests : IDisposable
{
    private readonly BookingFixture _f = new();

    public void Dispose() => _f.Dispose();

    private DateOnly InDays(int n) => _f.Today.AddDays(n);

    [Fact]
    public async Task A_valid_request_is_stored_as_requested()
    {
        var result = await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, BookingFixture.Days(InDays(2), 3), "For the shelves");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(BookingStatus.Requested, result.Booking!.Status);
        Assert.Equal(1, await _f.Db.Bookings.CountAsync());
    }

    [Fact]
    public async Task Overlapping_requests_for_the_same_tool_are_refused()
    {
        await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, BookingFixture.Days(InDays(2), 3), null);

        // Sharing only the last day is still a clash for an inclusive range.
        var clash = await _f.Bookings.RequestAsync(_f.Tool.Id, _f.OtherBorrower.Id, BookingFixture.Days(InDays(4), 2), null);
        var adjacent = await _f.Bookings.RequestAsync(_f.Tool.Id, _f.OtherBorrower.Id, BookingFixture.Days(InDays(5), 2), null);

        Assert.False(clash.Succeeded);
        Assert.True(adjacent.Succeeded, adjacent.Error);
    }

    [Fact]
    public async Task Owners_cannot_book_their_own_tools()
    {
        var result = await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Owner.Id, BookingFixture.Days(InDays(2), 1), null);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Paused_tools_cannot_be_booked()
    {
        _f.Tool.IsListed = false;
        await _f.Db.SaveChangesAsync();

        var result = await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, BookingFixture.Days(InDays(2), 1), null);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Requests_longer_than_the_owners_limit_are_refused()
    {
        var result = await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, BookingFixture.Days(InDays(2), 8), null);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Approving_a_request_declines_competing_ones_for_the_same_days()
    {
        var first = await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, BookingFixture.Days(InDays(2), 3), null);

        // A declined/cancelled booking releases its dates, so a second request can be made once
        // the first is out of the way; here we insert an overlapping one directly to model a race.
        _f.Db.Bookings.Add(new Booking
        {
            ToolId = _f.Tool.Id,
            BorrowerId = _f.OtherBorrower.Id,
            StartDate = InDays(3),
            EndDate = InDays(6),
            Status = BookingStatus.Requested
        });
        await _f.Db.SaveChangesAsync();

        var approved = await _f.Bookings.ApproveAsync(first.Booking!.Id, _f.Owner.Id, "Enjoy");

        Assert.True(approved.Succeeded, approved.Error);
        var all = await _f.Db.Bookings.AsNoTracking().OrderBy(b => b.Id).ToListAsync();
        Assert.Equal(BookingStatus.Approved, all[0].Status);
        Assert.Equal(BookingStatus.Declined, all[1].Status);
        Assert.Equal("Enjoy", all[0].OwnerNote);
    }

    [Fact]
    public async Task Only_the_owner_can_approve_or_decline()
    {
        var booking = (await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, BookingFixture.Days(InDays(2), 1), null)).Booking!;

        Assert.False((await _f.Bookings.ApproveAsync(booking.Id, _f.Borrower.Id, null)).Succeeded);
        Assert.False((await _f.Bookings.DeclineAsync(booking.Id, _f.OtherBorrower.Id, null)).Succeeded);
        Assert.True((await _f.Bookings.DeclineAsync(booking.Id, _f.Owner.Id, "Sorry")).Succeeded);
    }

    [Fact]
    public async Task A_decided_request_cannot_be_decided_again()
    {
        var booking = (await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, BookingFixture.Days(InDays(2), 1), null)).Booking!;
        await _f.Bookings.ApproveAsync(booking.Id, _f.Owner.Id, null);

        Assert.False((await _f.Bookings.ApproveAsync(booking.Id, _f.Owner.Id, null)).Succeeded);
        Assert.False((await _f.Bookings.DeclineAsync(booking.Id, _f.Owner.Id, null)).Succeeded);
    }

    [Fact]
    public async Task Cancelling_releases_the_dates_and_only_the_two_parties_may_cancel()
    {
        var range = BookingFixture.Days(InDays(2), 3);
        var booking = (await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, range, null)).Booking!;

        Assert.False((await _f.Bookings.CancelAsync(booking.Id, _f.OtherBorrower.Id)).Succeeded);
        Assert.True((await _f.Bookings.CancelAsync(booking.Id, _f.Borrower.Id)).Succeeded);

        var again = await _f.Bookings.RequestAsync(_f.Tool.Id, _f.OtherBorrower.Id, range, null);
        Assert.True(again.Succeeded, again.Error);
    }

    [Fact]
    public async Task Only_an_approved_loan_can_be_marked_returned_and_only_by_the_owner()
    {
        var booking = (await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, BookingFixture.Days(InDays(2), 1), null)).Booking!;

        Assert.False((await _f.Bookings.MarkReturnedAsync(booking.Id, _f.Owner.Id)).Succeeded);

        await _f.Bookings.ApproveAsync(booking.Id, _f.Owner.Id, null);
        Assert.False((await _f.Bookings.MarkReturnedAsync(booking.Id, _f.Borrower.Id)).Succeeded);
        Assert.True((await _f.Bookings.MarkReturnedAsync(booking.Id, _f.Owner.Id)).Succeeded);
    }

    [Fact]
    public async Task Held_ranges_ignore_declined_and_cancelled_bookings()
    {
        var kept = (await _f.Bookings.RequestAsync(_f.Tool.Id, _f.Borrower.Id, BookingFixture.Days(InDays(2), 1), null)).Booking!;
        var dropped = (await _f.Bookings.RequestAsync(_f.Tool.Id, _f.OtherBorrower.Id, BookingFixture.Days(InDays(5), 1), null)).Booking!;
        await _f.Bookings.DeclineAsync(dropped.Id, _f.Owner.Id, null);

        var held = await _f.Bookings.HeldRangesAsync(_f.Tool.Id);

        Assert.Equal(new[] { kept.Id }, held.Select(b => b.Id).ToArray());
    }

    [Fact]
    public void Overdue_means_approved_and_past_the_last_day()
    {
        var booking = new Booking { Status = BookingStatus.Approved, StartDate = new(2026, 5, 1), EndDate = new(2026, 5, 3) };

        Assert.False(booking.IsOverdueOn(new DateOnly(2026, 5, 3)));
        Assert.True(booking.IsOverdueOn(new DateOnly(2026, 5, 4)));

        booking.Status = BookingStatus.Returned;
        Assert.False(booking.IsOverdueOn(new DateOnly(2026, 5, 20)));

        booking.Status = BookingStatus.Requested;
        Assert.False(booking.IsOverdueOn(new DateOnly(2026, 5, 20)));
    }
}
