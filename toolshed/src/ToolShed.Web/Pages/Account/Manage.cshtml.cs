using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToolShed.Web.Models;

namespace ToolShed.Web.Pages.Account;

// Redundant next to the fallback policy, but it keeps the requirement visible
// on the one page in this folder that must not be anonymous.
[Authorize]
public class ManageModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public ManageModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [BindProperty]
    public ProfileInput Profile { get; set; } = new();

    [BindProperty]
    public PasswordInput Password { get; set; } = new();

    public string? Email { get; private set; }

    public class ProfileInput
    {
        [Required]
        [StringLength(80, MinimumLength = 2)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(120)]
        public string? Location { get; set; }
    }

    public class PasswordInput
    {
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "The two passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return NotFound();
        }

        Email = user.Email;
        Profile = new ProfileInput { DisplayName = user.DisplayName, Location = user.Location };
        return Page();
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return NotFound();
        }

        Email = user.Email;
        ModelState.Remove("Password.CurrentPassword");
        ModelState.Remove("Password.NewPassword");
        ModelState.Remove("Password.ConfirmPassword");

        if (!ModelState.IsValid)
        {
            return Page();
        }

        user.DisplayName = Profile.DisplayName.Trim();
        user.Location = string.IsNullOrWhiteSpace(Profile.Location) ? null : Profile.Location.Trim();
        await _userManager.UpdateAsync(user);

        TempData["Status"] = "Profile saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return NotFound();
        }

        Email = user.Email;
        ModelState.Remove("Profile.DisplayName");
        ModelState.Remove("Profile.Location");

        if (!ModelState.IsValid)
        {
            Profile = new ProfileInput { DisplayName = user.DisplayName, Location = user.Location };
            return Page();
        }

        var result = await _userManager.ChangePasswordAsync(user, Password.CurrentPassword, Password.NewPassword);
        if (!result.Succeeded)
        {
            Profile = new ProfileInput { DisplayName = user.DisplayName, Location = user.Location };
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        // Keep this session alive; the security stamp change signs out the others.
        await _signInManager.RefreshSignInAsync(user);
        TempData["Status"] = "Password changed. Any other signed-in devices have been signed out.";
        return RedirectToPage();
    }
}
