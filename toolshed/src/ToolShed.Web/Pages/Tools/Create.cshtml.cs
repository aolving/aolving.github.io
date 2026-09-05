using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Tools;

public class CreateModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly PhotoStorage _photos;

    public CreateModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager, PhotoStorage photos)
    {
        _db = db;
        _userManager = userManager;
        _photos = photos;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public List<IFormFile> Photos { get; set; } = [];

    public IReadOnlyList<string> KnownCategories { get; private set; } = [];

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
    }

    public async Task OnGetAsync() => await LoadCategoriesAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCategoriesAsync();

        if (Photos.Count > ImageValidator.MaxPhotosPerTool)
        {
            ModelState.AddModelError(nameof(Photos), $"Please attach no more than {ImageValidator.MaxPhotosPerTool} photos.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var tool = new Tool
        {
            OwnerId = _userManager.GetUserId(User)!,
            Name = Input.Name.Trim(),
            Category = Input.Category.Trim(),
            Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim(),
            PickupLocation = string.IsNullOrWhiteSpace(Input.PickupLocation) ? null : Input.PickupLocation.Trim(),
            MaxLoanDays = Input.MaxLoanDays
        };

        var saved = new List<ToolPhoto>();
        foreach (var file in Photos.Where(f => f.Length > 0))
        {
            var (photo, error) = await _photos.SaveAsync(file);
            if (photo is null)
            {
                // Do not leave orphaned bytes behind when one file in a batch is rejected.
                foreach (var orphan in saved)
                {
                    _photos.Delete(orphan);
                }

                ModelState.AddModelError(nameof(Photos), error ?? "That photo could not be read.");
                return Page();
            }

            photo.IsPrimary = saved.Count == 0;
            saved.Add(photo);
        }

        foreach (var photo in saved)
        {
            tool.Photos.Add(photo);
        }

        _db.Tools.Add(tool);
        await _db.SaveChangesAsync();

        TempData["Status"] = $"\"{tool.Name}\" is now in the catalogue.";
        return RedirectToPage("/Tools/Details", new { id = tool.Id });
    }

    private async Task LoadCategoriesAsync() =>
        KnownCategories = await _db.Tools.Select(t => t.Category).Distinct().OrderBy(c => c).ToListAsync();
}
