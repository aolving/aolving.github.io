using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;

namespace ToolShed.Web.Pages.Admin;

public class MembersModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public MembersModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public IReadOnlyList<Row> Members { get; private set; } = [];

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
