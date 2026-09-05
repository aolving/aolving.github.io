using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToolShed.Web.Models;

namespace ToolShed.Web.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(SignInManager<ApplicationUser> signInManager, ILogger<LoginModel> logger)
    {
        _signInManager = signInManager;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        // Clear any half-finished external cookie so a fresh sign-in starts clean.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(
            Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            _logger.LogInformation("Member signed in.");
            return LocalRedirect(SafeReturnUrl(returnUrl));
        }

        if (result.IsLockedOut)
        {
            _logger.LogWarning("Account locked out after repeated failures.");
            ModelState.AddModelError(string.Empty, "Too many failed attempts. Try again in 15 minutes.");
            return Page();
        }

        // Deliberately identical for a wrong password, an unknown address and an
        // unconfirmed account, so the form cannot be used to enumerate members.
        ModelState.AddModelError(string.Empty, "That email and password combination was not recognised.");
        return Page();
    }

    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
}
