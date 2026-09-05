using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;

namespace ToolShed.Web.Services;

public record BookingResult(bool Succeeded, string? Error, Booking? Booking = null)
{
    public static BookingResult Fail(string error) => new(false, error);

    public static BookingResult Ok(Booking booking) => new(true, null, booking);
}

/// <summary>
/// Every state change to a loan goes through here so the ownership checks and the
/// double-booking check live in exactly one place.
/// </summary>
public class BookingService
{
    /// <summary>Nobody needs to reserve a hedge trimmer for next summer.</summary>
    public const int MaxDaysAhead = 180;

    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<BookingService> _logger;

    public BookingService(ApplicationDbContext db, TimeProvider clock, ILogger<BookingService> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().Date);

    /// <summary>Validates a proposed loan against the calendar and the tool's own rules.</summary>
    public static string? ValidateRange(DateRange range, DateOnly today, int maxLoanDays)
    {
        if (!range.IsWellFormed)
        {
            return "The return date cannot be before the collection date.";
        }

        if (range.Start < today)
        {
            return "Loans cannot start in the past.";
        }

        if (range.Start.DayNumber - today.DayNumber > MaxDaysAhead)
        {
            return $"Loans can only be booked up to {MaxDaysAhead} days ahead.";
        }

        if (range.Days > maxLoanDays)
        {
            return $"This tool can be borrowed for at most {maxLoanDays} day(s) at a time.";
        }

        return null;
    }

    /// <summary>The bookings that currently hold dates on a tool, ignoring one being edited.</summary>
    public async Task<List<Booking>> HeldRangesAsync(int toolId, int? excludeBookingId = null)
    {
        return await _db.Bookings
            .Where(b => b.ToolId == toolId
                        && (b.Status == BookingStatus.Requested || b.Status == BookingStatus.Approved)
                        && (excludeBookingId == null || b.Id != excludeBookingId))
            .OrderBy(b => b.StartDate)
            .ToListAsync();
    }

    public async Task<BookingResult> RequestAsync(int toolId, string borrowerId, DateRange range, string? note)
    {
        var tool = await _db.Tools.FirstOrDefaultAsync(t => t.Id == toolId);
        if (tool is null)
        {
            return BookingResult.Fail("That tool no longer exists.");
        }

        if (!tool.IsListed)
        {
            return BookingResult.Fail("The owner has paused loans for this tool.");
        }

        if (tool.OwnerId == borrowerId)
        {
            return BookingResult.Fail("You already own this one.");
        }

        var rangeError = ValidateRange(range, Today, tool.MaxLoanDays);
        if (rangeError is not null)
        {
            return BookingResult.Fail(rangeError);
        }

        // Serialise the read-then-write so two members cannot claim the same days.
        await using var transaction = await _db.Database.BeginTransactionAsync();

        if (await ClashesAsync(toolId, range, null))
        {
            return BookingResult.Fail("Those dates are already spoken for. Pick a different window.");
        }

        var booking = new Booking
        {
            ToolId = toolId,
            BorrowerId = borrowerId,
            StartDate = range.Start,
            EndDate = range.End,
            BorrowerNote = note,
            Status = BookingStatus.Requested,
            RequestedUtc = _clock.GetUtcNow()
        };

        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation("Booking {Id} requested on tool {ToolId} by {BorrowerId}", booking.Id, toolId, borrowerId);
        return BookingResult.Ok(booking);
    }

    public async Task<BookingResult> ApproveAsync(int bookingId, string ownerId, string? ownerNote)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var booking = await LoadForOwnerAsync(bookingId, ownerId);
        if (booking is null)
        {
            return BookingResult.Fail("That request is not yours to approve.");
        }

        if (booking.Status != BookingStatus.Requested)
        {
            return BookingResult.Fail("That request has already been dealt with.");
        }

        var range = new DateRange(booking.StartDate, booking.EndDate);
        var clashesWithApproved = await _db.Bookings
            .AnyAsync(b => b.ToolId == booking.ToolId
                           && b.Id != booking.Id
                           && b.Status == BookingStatus.Approved
                           && b.StartDate <= range.End
                           && range.Start <= b.EndDate);

        if (clashesWithApproved)
        {
            return BookingResult.Fail("You have already approved another loan over those dates.");
        }

        var now = _clock.GetUtcNow();
        booking.Status = BookingStatus.Approved;
        booking.OwnerNote = ownerNote;
        booking.DecidedUtc = now;

        // Approving one request settles the competing ones for the same days.
        var competing = await _db.Bookings
            .Where(b => b.ToolId == booking.ToolId
                        && b.Id != booking.Id
                        && b.Status == BookingStatus.Requested
                        && b.StartDate <= range.End
                        && range.Start <= b.EndDate)
            .ToListAsync();

        foreach (var other in competing)
        {
            other.Status = BookingStatus.Declined;
            other.DecidedUtc = now;
            other.OwnerNote = "Automatically declined: the tool was lent to someone else for those dates.";
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation("Booking {Id} approved, {Count} competing request(s) declined", booking.Id, competing.Count);
        return BookingResult.Ok(booking);
    }

    public async Task<BookingResult> DeclineAsync(int bookingId, string ownerId, string? ownerNote)
    {
        var booking = await LoadForOwnerAsync(bookingId, ownerId);
        if (booking is null)
        {
            return BookingResult.Fail("That request is not yours to decline.");
        }

        if (booking.Status != BookingStatus.Requested)
        {
            return BookingResult.Fail("That request has already been dealt with.");
        }

        booking.Status = BookingStatus.Declined;
        booking.OwnerNote = ownerNote;
        booking.DecidedUtc = _clock.GetUtcNow();
        await _db.SaveChangesAsync();
        return BookingResult.Ok(booking);
    }

    /// <summary>Either party can call a loan off while it still holds dates.</summary>
    public async Task<BookingResult> CancelAsync(int bookingId, string userId)
    {
        var booking = await _db.Bookings
            .Include(b => b.Tool)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking is null || (booking.BorrowerId != userId && booking.Tool?.OwnerId != userId))
        {
            return BookingResult.Fail("That booking is not yours to cancel.");
        }

        if (!booking.HoldsDates)
        {
            return BookingResult.Fail("That booking is already closed.");
        }

        booking.Status = BookingStatus.Cancelled;
        booking.DecidedUtc = _clock.GetUtcNow();
        await _db.SaveChangesAsync();
        return BookingResult.Ok(booking);
    }

    public async Task<BookingResult> MarkReturnedAsync(int bookingId, string ownerId)
    {
        var booking = await LoadForOwnerAsync(bookingId, ownerId);
        if (booking is null)
        {
            return BookingResult.Fail("That loan is not yours to close.");
        }

        if (booking.Status != BookingStatus.Approved)
        {
            return BookingResult.Fail("Only an approved loan can be marked as returned.");
        }

        booking.Status = BookingStatus.Returned;
        booking.ReturnedUtc = _clock.GetUtcNow();
        await _db.SaveChangesAsync();
        return BookingResult.Ok(booking);
    }

    private Task<Booking?> LoadForOwnerAsync(int bookingId, string ownerId) =>
        _db.Bookings
            .Include(b => b.Tool)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.Tool!.OwnerId == ownerId);

    private Task<bool> ClashesAsync(int toolId, DateRange range, int? excludeBookingId) =>
        _db.Bookings.AnyAsync(b => b.ToolId == toolId
                                   && (b.Status == BookingStatus.Requested || b.Status == BookingStatus.Approved)
                                   && (excludeBookingId == null || b.Id != excludeBookingId)
                                   && b.StartDate <= range.End
                                   && range.Start <= b.EndDate);
}
