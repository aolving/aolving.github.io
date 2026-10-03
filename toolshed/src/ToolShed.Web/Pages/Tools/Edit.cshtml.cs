using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Contracts;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Tools;

public class EditModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ToolService _tools;

    public EditModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager, ToolService tools)
    {
        _db = db;
        _userManager = userManager;
        _tools = tools;
    }

    private string UserId => _userManager.GetUserId(User)!;

    public Tool Tool { get; private set; } = null!;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public List<IFormFile> NewPhotos { get; set; } = [];

    public class InputModel
    {
        [Required]
        [StringLength(120, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(60, MinimumLength = 2)]
        public string Category { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Description { get; set; }

        [StringLength(120)]
        [Display(Name = "Collection point")]
        public string? PickupLocation { get; set; }

        [Range(1, 90)]
        [Display(Name = "Longest single loan")]
        public int MaxLoanDays { get; set; } = 14;

        [Display(Name = "Listed")]
        public bool IsListed { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadOwnedAsync(id))
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Name = Tool.Name,
            Category = Tool.Category,
            Description = Tool.Description,
            PickupLocation = Tool.PickupLocation,
            MaxLoanDays = Tool.MaxLoanDays,
            IsListed = Tool.IsListed
        };

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id)
    {
        if (!await LoadOwnedAsync(id))
        {
            return NotFound();
        }

        ModelState.Remove(nameof(NewPhotos));
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var input = new ToolInput(Input.Name, Input.Category, Input.Description, Input.PickupLocation, Input.MaxLoanDays, Input.IsListed);
        var result = await _tools.UpdateAsync(id, UserId, input);
        if (result.Failure == ToolFailure.NotFound)
        {
            return NotFound();
        }

        TempData["Status"] = "Listing updated.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddPhotosAsync(int id)
    {
        var result = await _tools.AddPhotosAsync(id, UserId, NewPhotos);
        if (result.Failure == ToolFailure.NotFound)
        {
            return NotFound();
        }

        if (result.Succeeded)
        {
            TempData["Status"] = "Photos added.";
        }
        else
        {
            TempData["Error"] = result.Error;
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMakePrimaryAsync(int id, int photoId)
    {
        var result = await _tools.SetCoverAsync(id, UserId, photoId);
        if (!result.Succeeded)
        {
            return NotFound();
        }

        TempData["Status"] = "Cover photo changed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeletePhotoAsync(int id, int photoId)
    {
        var result = await _tools.DeletePhotoAsync(id, UserId, photoId);
        if (!result.Succeeded)
        {
            return NotFound();
        }

        TempData["Status"] = "Photo removed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var result = await _tools.DeleteAsync(id, UserId);
        if (!result.Succeeded)
        {
            return NotFound();
        }

        TempData["Status"] = "Listing deleted.";
        return RedirectToPage("/Tools/Mine");
    }

    /// <summary>Loads the tool only if the signed-in member owns it — every handler starts here.</summary>
    private async Task<bool> LoadOwnedAsync(int id)
    {
        var tool = await _db.Tools
            .Include(t => t.Photos)
            .FirstOrDefaultAsync(t => t.Id == id && t.OwnerId == UserId);

        if (tool is null)
        {
            return false;
        }

        Tool = tool;
        return true;
    }
}
