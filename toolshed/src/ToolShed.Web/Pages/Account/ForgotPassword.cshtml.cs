using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Account;

[AllowAnonymous]
public class ForgotPasswordModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;

    public ForgotPasswordModel(UserManager<ApplicationUser> userManager, IEmailService email, IConfiguration configuration)
    {
        _userManager = userManager;
        _email = email;
        _configuration = configuration;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool EmailEnabled => _email.IsConfigured;

    public bool Submitted { get; private set; }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!EmailEnabled)
        {
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _userManager.FindByEmailAsync(Input.Email.Trim());
        var suspended = user?.LockoutEnd > DateTimeOffset.UtcNow;

        if (user is not null && !suspended)
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var link = Url.Page("/Account/ResetPassword", pageHandler: null,
                values: new { userId = user.Id, code = EncodeToken(token) }, protocol: Request.Scheme)!;

            var portal = _configuration["Portal:Name"] ?? "The Tool Shed";
            await _email.SendAsync(user.Email!, $"Reset your {portal} password",
                $"Someone asked to reset the password for this {portal} account.\n\n" +
                $"Choose a new password here (the link works for two hours):\n{link}\n\n" +
                "If that was not you, ignore this message and your password stays as it is.");
        }

        // The same answer whether or not the address is a member, so the form cannot be used to find out.
        Submitted = true;
        return Page();
    }

    public static string EncodeToken(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
}
