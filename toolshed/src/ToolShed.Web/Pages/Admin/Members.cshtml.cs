using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Pages.Account;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Admin;

public class MembersModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;

    public MembersModel(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        IEmailService email,
        IConfiguration configuration)
    {
        _db = db;
        _userManager = userManager;
        _email = email;
        _configuration = configuration;
    }

    public IReadOnlyList<Row> Members { get; private set; } = [];

    /// <summary>Only ever in TempData: the token is not stored anywhere, so this is the one time it exists.</summary>
    public string? ResetLink => TempData["ResetLink"] as string;

    public string? ResetFor => TempData["ResetFor"] as string;

    public record Row(string Id, string DisplayName, string? Email, string Role, DateTimeOffset JoinedUtc, bool IsSuspended, bool IsSelf);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostToggleAsync(string userId)
    {
        if (userId == _userManager.GetUserId(User))
        {
            TempData["Error"] = "You cannot suspend your own account.";
            return RedirectToPage();
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var suspended = user.LockoutEnd > DateTimeOffset.UtcNow;
        if (suspended)
        {
            await _userManager.SetLockoutEndDateAsync(user, null);
            TempData["Status"] = $"{user.DisplayName} can sign in again.";
        }
        else
        {
            // Far-future lockout end is Identity's own way of parking an account.
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            // Rolls the security stamp so any live cookie stops working.
            await _userManager.UpdateSecurityStampAsync(user);
            TempData["Status"] = $"{user.DisplayName} is suspended.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetRoleAsync(string userId, string role)
    {
        // An admin cannot change their own role, so the portal can never be left without one.
        if (userId == _userManager.GetUserId(User))
        {
            TempData["Error"] = "You cannot change your own role.";
            return RedirectToPage();
        }

        if (!Roles.All.Contains(role))
        {
            return BadRequest();
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var current = await _userManager.GetRolesAsync(user);
        if (!current.Contains(role))
        {
            await _userManager.RemoveFromRolesAsync(user, current);
            await _userManager.AddToRoleAsync(user, role);
            // Roles are baked into the cookie, so make the member sign in again to pick up the change.
            await _userManager.UpdateSecurityStampAsync(user);
        }

        TempData["Status"] = $"{user.DisplayName} is now {(role == Roles.Admin ? "an admin" : "a member")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetLinkAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var link = Url.Page("/Account/ResetPassword", pageHandler: null,
            values: new { userId = user.Id, code = ForgotPasswordModel.EncodeToken(token) }, protocol: Request.Scheme)!;

        TempData["ResetLink"] = link;
        TempData["ResetFor"] = user.Email;

        if (_email.IsConfigured && !string.IsNullOrWhiteSpace(user.Email))
        {
            var portal = _configuration["Portal:Name"] ?? "The Tool Shed";
            var sent = await _email.SendAsync(user.Email, $"Reset your {portal} password",
                $"An administrator started a password reset for your {portal} account.\n\n" +
                $"Choose a new password here (the link works for two hours):\n{link}");
            TempData["Status"] = sent ? "Reset link created and emailed." : "Reset link created, but the email could not be sent.";
        }
        else
        {
            TempData["Status"] = "Reset link created.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var currentUserId = _userManager.GetUserId(User);
        var now = DateTimeOffset.UtcNow;

        var users = await _db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync();

        var roles = await (from userRole in _db.UserRoles
                join role in _db.Roles on userRole.RoleId equals role.Id
                select new { userRole.UserId, role.Name })
            .ToListAsync();

        Members = users.Select(u => new Row(
            u.Id,
            u.DisplayName,
            u.Email,
            roles.FirstOrDefault(r => r.UserId == u.Id)?.Name ?? Roles.Member,
            u.JoinedUtc,
            u.LockoutEnd > now,
            u.Id == currentUserId)).ToList();
    }
}
