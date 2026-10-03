using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using ToolShed.Web.Models;

namespace ToolShed.Web.Pages.Account;

[AllowAnonymous]
public class ResetPasswordModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;

    public ResetPasswordModel(UserManager<ApplicationUser> userManager) => _userManager = userManager;

    [BindProperty(SupportsGet = true)]
    public string? UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool LinkIsBroken { get; private set; }

    public class InputModel
    {
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

    public IActionResult OnGet()
    {
        LinkIsBroken = !TryDecode(Code, out _) || string.IsNullOrWhiteSpace(UserId);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!TryDecode(Code, out var token) || string.IsNullOrWhiteSpace(UserId))
        {
            LinkIsBroken = true;
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _userManager.FindByIdAsync(UserId);
        if (user is null)
        {
            // Same outcome as a bad token: do not reveal that the account does not exist.
            LinkIsBroken = true;
            return Page();
        }

        var result = await _userManager.ResetPasswordAsync(user, token, Input.NewPassword);
        if (result.Succeeded)
        {
            TempData["Status"] = "Password updated. You can sign in now.";
            return RedirectToPage("/Account/Login");
        }

        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
        {
            LinkIsBroken = true;
            return Page();
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        return Page();
    }

    private static bool TryDecode(string? code, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
