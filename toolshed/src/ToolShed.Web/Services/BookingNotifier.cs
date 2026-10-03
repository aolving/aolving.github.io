using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;

namespace ToolShed.Web.Services;

public enum BookingEvent
{
    Requested,
    Approved,
    Declined,
    Cancelled,
    Returned
}

/// <summary>
/// Tells the other party about a change to a loan. Email is a courtesy: a failure to
/// send never fails the booking action, and with no SMTP host it does nothing.
/// </summary>
public class BookingNotifier
{
    private readonly ApplicationDbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BookingNotifier> _logger;

    public BookingNotifier(
        ApplicationDbContext db,
        IEmailService email,
        IConfiguration configuration,
        ILogger<BookingNotifier> logger)
    {
        _db = db;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task NotifyAsync(int bookingId, BookingEvent bookingEvent, string actorId, string loansUrl)
    {
        if (!_email.IsConfigured)
        {
            return;
        }

        try
        {
            var booking = await _db.Bookings.AsNoTracking()
                .Include(b => b.Tool).ThenInclude(t => t!.Owner)
                .Include(b => b.Borrower)
                .FirstOrDefaultAsync(b => b.Id == bookingId);

            if (booking?.Tool?.Owner is null || booking.Borrower is null)
            {
                return;
            }

            var recipient = RecipientFor(bookingEvent, booking, actorId);
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                return;
            }

            var portal = _configuration["Portal:Name"] ?? "The Tool Shed";
            var (subject, body) = Compose(bookingEvent, booking, portal, loansUrl);
            await _email.SendAsync(recipient.Email, subject, body);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not prepare a notification for booking {BookingId}", bookingId);
        }
    }

    internal static ApplicationUser RecipientFor(BookingEvent bookingEvent, Booking booking, string actorId)
    {
        var owner = booking.Tool!.Owner!;
        var borrower = booking.Borrower!;

        return bookingEvent switch
        {
            BookingEvent.Requested => owner,
            BookingEvent.Cancelled => actorId == booking.BorrowerId ? owner : borrower,
            _ => borrower
        };
    }

    internal static (string Subject, string Body) Compose(BookingEvent bookingEvent, Booking booking, string portal, string loansUrl)
    {
        var tool = booking.Tool!.Name;
        var owner = booking.Tool.Owner!.DisplayName;
        var borrower = booking.Borrower!.DisplayName;
        var dates = $"{booking.StartDate:d MMM yyyy} to {booking.EndDate:d MMM yyyy}";
        var note = string.IsNullOrWhiteSpace(booking.OwnerNote) ? string.Empty : $"\nNote from {owner}: {booking.OwnerNote}\n";

        var (subject, lead) = bookingEvent switch
        {
            BookingEvent.Requested => (
                $"{borrower} would like to borrow your {tool}",
                $"{borrower} has asked to borrow {tool} from {dates}."
                + (string.IsNullOrWhiteSpace(booking.BorrowerNote) ? string.Empty : $"\nTheir note: {booking.BorrowerNote}")),
            BookingEvent.Approved => (
                $"Your request for {tool} was approved",
                $"{owner} approved your request for {tool}, {dates}.{note}"),
            BookingEvent.Declined => (
                $"Your request for {tool} was declined",
                $"{owner} declined your request for {tool}, {dates}.{note}"),
            BookingEvent.Cancelled => (
                $"The loan of {tool} was cancelled",
                $"The loan of {tool} for {dates} has been cancelled."),
            _ => (
                $"{tool} is marked as returned",
                $"{owner} marked {tool} as returned. Thank you for looking after it.")
        };

        var body = $"{lead}\n\nOpen your loans: {loansUrl}\n\n-- {portal}";
        return (subject, body);
    }
}
