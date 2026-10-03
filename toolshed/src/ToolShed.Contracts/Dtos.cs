using System.ComponentModel.DataAnnotations;

namespace ToolShed.Contracts;

// ---- Authentication ----------------------------------------------------------------

public record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password,
    [property: StringLength(80)] string? DeviceName = null);

/// <summary>
/// Creates an account from inside the app. It takes the same three things as the website: the email the
/// member was invited with, the six-digit access code issued with that invitation, and a password they choose.
/// </summary>
public record RegisterRequest(
    [property: Required, EmailAddress, StringLength(200)] string Email,
    [property: Required] string AccessCode,
    [property: Required, StringLength(80, MinimumLength = 2)] string DisplayName,
    [property: Required] string Password,
    [property: StringLength(120)] string? Location = null,
    [property: StringLength(80)] string? DeviceName = null);

public record UserDto(string Id, string DisplayName, string Email, string? Location, bool IsAdmin);

public record AuthResponse(string Token, DateTimeOffset ExpiresUtc, UserDto User);

// ---- Tools -------------------------------------------------------------------------

public record ToolSummaryDto(
    int Id,
    string Name,
    string Category,
    string OwnerName,
    bool IsMine,
    bool IsListed,
    int? CoverPhotoId,
    DateOnly? OnLoanUntil);

public record PhotoDto(int Id, string? Caption, bool IsCover);

/// <summary>A stretch of days that is not free. Approved means confirmed; otherwise it is only requested.</summary>
public record HeldRangeDto(DateOnly Start, DateOnly End, bool Approved);

public record ToolDetailDto(
    int Id,
    string Name,
    string Category,
    string? Description,
    string? PickupLocation,
    int MaxLoanDays,
    bool IsListed,
    string OwnerName,
    bool IsMine,
    IReadOnlyList<PhotoDto> Photos,
    IReadOnlyList<HeldRangeDto> Held);

public record ToolInput(
    [property: Required, StringLength(120, MinimumLength = 2)] string Name,
    [property: Required, StringLength(60, MinimumLength = 2)] string Category,
    [property: StringLength(2000)] string? Description = null,
    [property: StringLength(120)] string? PickupLocation = null,
    [property: Range(1, 90)] int MaxLoanDays = 14,
    bool IsListed = true);

// ---- Bookings ----------------------------------------------------------------------

public record BookingRequestInput(DateOnly StartDate, DateOnly EndDate, [property: StringLength(1000)] string? Note = null);

public record DecisionInput([property: StringLength(1000)] string? Note = null);

public record BookingDto(
    int Id,
    int ToolId,
    string ToolName,
    string OtherPartyName,
    bool IAmOwner,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    bool IsOverdue,
    string? BorrowerNote,
    string? OwnerNote);

public record BookingsDto(IReadOnlyList<BookingDto> Incoming, IReadOnlyList<BookingDto> Outgoing);

// ---- Errors ------------------------------------------------------------------------

/// <summary>Every non-2xx API response carries one of these, so the app can show the message as-is.</summary>
public record ApiError(string Error);

public static class BookingStatuses
{
    public const string Requested = "Requested";
    public const string Approved = "Approved";
    public const string Declined = "Declined";
    public const string Cancelled = "Cancelled";
    public const string Returned = "Returned";
}
