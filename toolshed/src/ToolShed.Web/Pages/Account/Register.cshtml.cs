using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Account;

/// <summary>
/// The only way an account is created. Without a live invitation token this page
/// shows nothing but an apology — there is no open registration form to find.
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

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool TokenIsValid { get; private set; }

    public string? InvitedEmail { get; private set; }

    public class InputModel
    {
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

    public async Task<IActionResult> OnGetAsync()
    {
        var invitation = await _invitations.FindUsableAsync(Token);
        TokenIsValid = invitation is not null;
        InvitedEmail = invitation?.Email.ToLowerInvariant();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Re-check the token on every post: it may have been revoked or spent
        // between loading the form and submitting it.
        var invitation = await _invitations.FindUsableAsync(Token);
        if (invitation is null)
        {
            TokenIsValid = false;
            return Page();
        }

        TokenIsValid = true;
        InvitedEmail = invitation.Email.ToLowerInvariant();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The address comes from the invitation, never from the form, so a token
        // cannot be redirected to a different mailbox.
        var user = new ApplicationUser
        {
            UserName = InvitedEmail,
            Email = InvitedEmail,
            DisplayName = Input.DisplayName.Trim(),
            Location = string.IsNullOrWhiteSpace(Input.Location) ? null : Input.Location.Trim(),
            // The invitation was delivered to this mailbox, which is the proof of ownership.
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
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
