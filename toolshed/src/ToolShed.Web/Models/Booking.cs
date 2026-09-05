using System.ComponentModel.DataAnnotations;

namespace ToolShed.Web.Models;

public enum BookingStatus
{
    Requested = 0,
    Approved = 1,
    Declined = 2,
    Cancelled = 3,
    Returned = 4
}

public class Booking
{
    public int Id { get; set; }

    public int ToolId { get; set; }

    public Tool? Tool { get; set; }

    [Required]
    public string BorrowerId { get; set; } = string.Empty;

    public ApplicationUser? Borrower { get; set; }

    /// <summary>First day of the loan, inclusive.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Last day of the loan, inclusive. A one-day loan has Start == End.</summary>
    public DateOnly EndDate { get; set; }

    public BookingStatus Status { get; set; } = BookingStatus.Requested;

    [StringLength(1000)]
    public string? BorrowerNote { get; set; }

    [StringLength(1000)]
    public string? OwnerNote { get; set; }

    public DateTimeOffset RequestedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DecidedUtc { get; set; }

    public DateTimeOffset? ReturnedUtc { get; set; }

    /// <summary>Requested and approved loans both hold the dates; the rest release them.</summary>
    public bool HoldsDates => Status is BookingStatus.Requested or BookingStatus.Approved;

    public int Days => EndDate.DayNumber - StartDate.DayNumber + 1;
}
