using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Account;

/// <summary>
/// The only way an account is created. Joining takes the email you were invited with, a password you
/// choose, and the six-digit access code issued with the invitation. All three have to line up.
/// </summary>
[AllowAnonymous]
public class RegisterModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly InvitationService _invitations;
    private readonly ILogger<RegisterModel> _logger;

    public RegisterModel(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        InvitationService invitations,
        ILogger<RegisterModel> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _invitations = invitations;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [EmailAddress]
        [StringLength(200)]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter your six-digit access code.")]
        [Display(Name = "Access code")]
        public string AccessCode { get; set; } = string.Empty;

        [Required]
        [StringLength(80, MinimumLength = 2)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(120)]
        public string? Location { get; set; }

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    /// <summary>The invitation link carries the address, so it is filled in; the code is never in a link.</summary>
    public void OnGet(string? email) => Input.Email = email?.Trim() ?? string.Empty;

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Checked before anything is created, and with one answer for every kind of failure.
        var invitation = await _invitations.VerifyAsync(Input.Email, Input.AccessCode);
        if (invitation is null)
        {
            ModelState.AddModelError(string.Empty, InvitationService.NotValidMessage);
            Input.AccessCode = string.Empty;
            return Page();
        }

        // An invitation tied to an address creates that address; an open code uses the one typed.
        var email = InvitationService.AccountEmail(invitation, Input.Email);
        if (invitation.IsOpen && await _userManager.FindByEmailAsync(email) is not null)
        {
            // Not redeemed, so the code is still good for a different address.
            ModelState.AddModelError(nameof(Input.Email), "An account with that email already exists. Try signing in instead.");
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = Input.DisplayName.Trim(),
            Location = string.IsNullOrWhiteSpace(Input.Location) ? null : Input.Location.Trim(),
            // An invitation tied to an address names the mailbox; an open code was handed over by an
            // administrator who vouched for the holder. Either way the portal has no mailbox check of its own.
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            // A weak password does not use up the invitation; the code still works for the next try.
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        await _userManager.AddToRoleAsync(user, invitation.Role);
        await _invitations.RedeemAsync(invitation, user.Id);

        _logger.LogInformation("Member {UserId} joined via invitation {InvitationId}", user.Id, invitation.Id);

        await _signInManager.SignInAsync(user, isPersistent: false);
        return RedirectToPage("/Index");
    }
}
