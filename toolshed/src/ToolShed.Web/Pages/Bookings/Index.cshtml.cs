using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Bookings;

public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly BookingService _bookings;
    private readonly BookingNotifier _notifier;

    public IndexModel(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        BookingService bookings,
        BookingNotifier notifier)
    {
        _db = db;
        _userManager = userManager;
        _bookings = bookings;
        _notifier = notifier;
    }

    public DateOnly Today => _bookings.Today;

    /// <summary>Requests from other members for tools this member owns.</summary>
    public IReadOnlyList<Booking> Incoming { get; private set; } = [];

    /// <summary>This member's own requests for other people's tools.</summary>
    public IReadOnlyList<Booking> Outgoing { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public Task<IActionResult> OnPostApproveAsync(int bookingId, string? ownerNote) =>
        ActAsync(BookingEvent.Approved, userId => _bookings.ApproveAsync(bookingId, userId, CleanNote(ownerNote)));

    public Task<IActionResult> OnPostDeclineAsync(int bookingId, string? ownerNote) =>
        ActAsync(BookingEvent.Declined, userId => _bookings.DeclineAsync(bookingId, userId, CleanNote(ownerNote)));

    public Task<IActionResult> OnPostReturnedAsync(int bookingId) =>
        ActAsync(BookingEvent.Returned, userId => _bookings.MarkReturnedAsync(bookingId, userId));

    public Task<IActionResult> OnPostCancelAsync(int bookingId) =>
        ActAsync(BookingEvent.Cancelled, userId => _bookings.CancelAsync(bookingId, userId));

    public string PillClass(BookingStatus status) => status switch
    {
        BookingStatus.Approved => "pill-free",
        BookingStatus.Requested => "pill-alert",
        BookingStatus.Returned => "pill-done",
        _ => "pill-busy"
    };

    private static string? CleanNote(string? note)
    {
        var trimmed = note?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > 1000 ? trimmed[..1000] : trimmed;
    }

    private async Task<IActionResult> ActAsync(BookingEvent bookingEvent, Func<string, Task<BookingResult>> action)
    {
        var userId = _userManager.GetUserId(User)!;
        var result = await action(userId);

        if (result.Succeeded)
        {
            TempData["Status"] = "Done.";
            var loansUrl = Url.Page("/Bookings/Index", pageHandler: null, values: null, protocol: Request.Scheme)!;
            await _notifier.NotifyAsync(result.Booking!.Id, bookingEvent, userId, loansUrl);
        }
        else
        {
            TempData["Error"] = result.Error;
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var userId = _userManager.GetUserId(User)!;

        Incoming = await _db.Bookings.AsNoTracking()
            .Include(b => b.Tool)
            .Include(b => b.Borrower)
            .Where(b => b.Tool!.OwnerId == userId)
            .OrderBy(b => b.Status == BookingStatus.Requested ? 0 : 1)
            .ThenBy(b => b.StartDate)
            .ToListAsync();

        Outgoing = await _db.Bookings.AsNoTracking()
            .Include(b => b.Tool)
            .ThenInclude(t => t!.Owner)
            .Where(b => b.BorrowerId == userId)
            .OrderByDescending(b => b.StartDate)
            .ToListAsync();
    }
}
