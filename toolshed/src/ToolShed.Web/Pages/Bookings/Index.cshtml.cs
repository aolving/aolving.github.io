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

    public IndexModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager, BookingService bookings)
    {
        _db = db;
        _userManager = userManager;
        _bookings = bookings;
    }

    /// <summary>Requests from other members for tools this member owns.</summary>
    public IReadOnlyList<Booking> Incoming { get; private set; } = [];

    /// <summary>This member's own requests for other people's tools.</summary>
    public IReadOnlyList<Booking> Outgoing { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public Task<IActionResult> OnPostApproveAsync(int bookingId) =>
        ActAsync(userId => _bookings.ApproveAsync(bookingId, userId, null));

    public Task<IActionResult> OnPostDeclineAsync(int bookingId) =>
        ActAsync(userId => _bookings.DeclineAsync(bookingId, userId, null));

    public Task<IActionResult> OnPostReturnedAsync(int bookingId) =>
        ActAsync(userId => _bookings.MarkReturnedAsync(bookingId, userId));

    public Task<IActionResult> OnPostCancelAsync(int bookingId) =>
        ActAsync(userId => _bookings.CancelAsync(bookingId, userId));

    public string PillClass(BookingStatus status) => status switch
    {
        BookingStatus.Approved => "pill-free",
        BookingStatus.Requested => "pill-alert",
        BookingStatus.Returned => "pill-done",
        _ => "pill-busy"
    };

    private async Task<IActionResult> ActAsync(Func<string, Task<BookingResult>> action)
    {
        var userId = _userManager.GetUserId(User)!;
        var result = await action(userId);

        if (result.Succeeded)
        {
            TempData["Status"] = "Done.";
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
