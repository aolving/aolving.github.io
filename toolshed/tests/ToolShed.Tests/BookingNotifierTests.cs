using ToolShed.Web.Models;
using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

public class BookingNotifierTests
{
    private static Booking Sample() => new()
    {
        BorrowerId = "borrower-id",
        StartDate = new DateOnly(2026, 5, 4),
        EndDate = new DateOnly(2026, 5, 6),
        BorrowerNote = "For the shelves",
        OwnerNote = "Mind the battery",
        Tool = new Tool
        {
            Name = "Cordless drill",
            OwnerId = "owner-id",
            Owner = new ApplicationUser { Id = "owner-id", DisplayName = "Olive", Email = "olive@example.test" }
        },
        Borrower = new ApplicationUser { Id = "borrower-id", DisplayName = "Ben", Email = "ben@example.test" }
    };

    [Theory]
    [InlineData(BookingEvent.Requested, "olive@example.test")]
    [InlineData(BookingEvent.Approved, "ben@example.test")]
    [InlineData(BookingEvent.Declined, "ben@example.test")]
    [InlineData(BookingEvent.Returned, "ben@example.test")]
    public void Each_event_goes_to_the_party_who_needs_to_know(BookingEvent evt, string expected)
    {
        Assert.Equal(expected, BookingNotifier.RecipientFor(evt, Sample(), "anyone").Email);
    }

    [Fact]
    public void A_cancellation_goes_to_whoever_did_not_cancel()
    {
        var booking = Sample();

        Assert.Equal("olive@example.test", BookingNotifier.RecipientFor(BookingEvent.Cancelled, booking, "borrower-id").Email);
        Assert.Equal("ben@example.test", BookingNotifier.RecipientFor(BookingEvent.Cancelled, booking, "owner-id").Email);
    }

    [Fact]
    public void Messages_name_the_tool_the_dates_and_link_to_the_loans_page()
    {
        var (subject, body) = BookingNotifier.Compose(BookingEvent.Requested, Sample(), "The Tool Shed", "https://shed.test/Bookings");

        Assert.Contains("Cordless drill", subject);
        Assert.Contains("4 May 2026", body);
        Assert.Contains("For the shelves", body);
        Assert.Contains("https://shed.test/Bookings", body);
    }

    [Fact]
    public void The_owners_note_reaches_the_borrower_on_approval()
    {
        var (_, body) = BookingNotifier.Compose(BookingEvent.Approved, Sample(), "The Tool Shed", "https://shed.test/Bookings");
        Assert.Contains("Mind the battery", body);
    }
}
