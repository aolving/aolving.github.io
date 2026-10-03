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

public class CreateModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ToolService _tools;

    public CreateModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager, ToolService tools)
    {
        _db = db;
        _userManager = userManager;
        _tools = tools;
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

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var input = new ToolInput(Input.Name, Input.Category, Input.Description, Input.PickupLocation, Input.MaxLoanDays);
        var result = await _tools.CreateAsync(_userManager.GetUserId(User)!, input, Photos);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(nameof(Photos), result.Error ?? "That photo could not be read.");
            return Page();
        }

        TempData["Status"] = $"\"{result.Tool!.Name}\" is now in the catalogue.";
        return RedirectToPage("/Tools/Details", new { id = result.Tool.Id });
    }

    private async Task LoadCategoriesAsync() =>
        KnownCategories = await _db.Tools.Select(t => t.Category).Distinct().OrderBy(c => c).ToListAsync();
}
