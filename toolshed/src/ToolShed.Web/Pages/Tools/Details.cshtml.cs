using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Tools;

public class DetailsModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly BookingService _bookings;

    public DetailsModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager, BookingService bookings)
    {
        _db = db;
        _userManager = userManager;
        _bookings = bookings;
    }

    public Tool Tool { get; private set; } = null!;

    public IReadOnlyList<Booking> Held { get; private set; } = [];

    public bool IsOwner { get; private set; }

    public DateOnly Today => _bookings.Today;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [DataType(DataType.Date)]
        [Display(Name = "Collect on")]
        public DateOnly StartDate { get; set; }

        [Required]
        [DataType(DataType.Date)]
        [Display(Name = "Return by")]
        public DateOnly EndDate { get; set; }

        [StringLength(1000)]
        public string? Note { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Input.StartDate = Today;
        Input.EndDate = Today.AddDays(2);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (IsOwner)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var borrowerId = _userManager.GetUserId(User)!;
        var result = await _bookings.RequestAsync(
            id, borrowerId, new DateRange(Input.StartDate, Input.EndDate), Input.Note?.Trim());

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            Held = await _bookings.HeldRangesAsync(id);
            return Page();
        }

        TempData["Status"] = "Request sent. The owner will see it under Loans.";
        return RedirectToPage("/Bookings/Index");
    }

    private async Task<bool> LoadAsync(int id)
    {
        var tool = await _db.Tools
            .Include(t => t.Owner)
            .Include(t => t.Photos)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tool is null)
        {
            return false;
        }

        Tool = tool;
        IsOwner = tool.OwnerId == _userManager.GetUserId(User);

        // A paused tool drops out of the catalogue but stays readable on a direct
        // link, so anyone mid-loan can still find the owner and the pickup point.
        Held = await _bookings.HeldRangesAsync(id);
        return true;
    }
}
