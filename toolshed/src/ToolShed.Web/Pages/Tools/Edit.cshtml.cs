using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Tools;

public class EditModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly PhotoStorage _photos;

    public EditModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager, PhotoStorage photos)
    {
        _db = db;
        _userManager = userManager;
        _photos = photos;
    }

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

        Tool.Name = Input.Name.Trim();
        Tool.Category = Input.Category.Trim();
        Tool.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        Tool.PickupLocation = string.IsNullOrWhiteSpace(Input.PickupLocation) ? null : Input.PickupLocation.Trim();
        Tool.MaxLoanDays = Input.MaxLoanDays;
        Tool.IsListed = Input.IsListed;
        Tool.UpdatedUtc = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        TempData["Status"] = "Listing updated.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddPhotosAsync(int id)
    {
        if (!await LoadOwnedAsync(id))
        {
            return NotFound();
        }

        var room = ImageValidator.MaxPhotosPerTool - Tool.Photos.Count;
        var incoming = NewPhotos.Where(f => f.Length > 0).ToList();

        if (incoming.Count == 0)
        {
            TempData["Error"] = "Pick at least one photo to upload.";
            return RedirectToPage(new { id });
        }

        if (incoming.Count > room)
        {
            TempData["Error"] = $"There is only room for {room} more photo(s) on this listing.";
            return RedirectToPage(new { id });
        }

        foreach (var file in incoming)
        {
            var (photo, error) = await _photos.SaveAsync(file);
            if (photo is null)
            {
                TempData["Error"] = error;
                return RedirectToPage(new { id });
            }

            photo.IsPrimary = Tool.Photos.Count == 0;
            Tool.Photos.Add(photo);
            await _db.SaveChangesAsync();
        }

        TempData["Status"] = "Photos added.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMakePrimaryAsync(int id, int photoId)
    {
        if (!await LoadOwnedAsync(id))
        {
            return NotFound();
        }

        var target = Tool.Photos.FirstOrDefault(p => p.Id == photoId);
        if (target is null)
        {
            return NotFound();
        }

        foreach (var photo in Tool.Photos)
        {
            photo.IsPrimary = photo.Id == photoId;
        }

        await _db.SaveChangesAsync();
        TempData["Status"] = "Cover photo changed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeletePhotoAsync(int id, int photoId)
    {
        if (!await LoadOwnedAsync(id))
        {
            return NotFound();
        }

        var target = Tool.Photos.FirstOrDefault(p => p.Id == photoId);
        if (target is null)
        {
            return NotFound();
        }

        var wasPrimary = target.IsPrimary;
        _db.ToolPhotos.Remove(target);
        await _db.SaveChangesAsync();
        _photos.Delete(target);

        if (wasPrimary)
        {
            var replacement = await _db.ToolPhotos.Where(p => p.ToolId == id).OrderBy(p => p.Id).FirstOrDefaultAsync();
            if (replacement is not null)
            {
                replacement.IsPrimary = true;
                await _db.SaveChangesAsync();
            }
        }

        TempData["Status"] = "Photo removed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!await LoadOwnedAsync(id))
        {
            return NotFound();
        }

        var files = Tool.Photos.ToList();
        _db.Tools.Remove(Tool);
        await _db.SaveChangesAsync();

        foreach (var photo in files)
        {
            _photos.Delete(photo);
        }

        TempData["Status"] = "Listing deleted.";
        return RedirectToPage("/Tools/Mine");
    }

    /// <summary>Loads the tool only if the signed-in member owns it — every handler starts here.</summary>
    private async Task<bool> LoadOwnedAsync(int id)
    {
        var userId = _userManager.GetUserId(User);
        var tool = await _db.Tools
            .Include(t => t.Photos)
            .FirstOrDefaultAsync(t => t.Id == id && t.OwnerId == userId);

        if (tool is null)
        {
            return false;
        }

        Tool = tool;
        return true;
    }
}
