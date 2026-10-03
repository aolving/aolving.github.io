using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Admin;

public class InvitationsModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly InvitationService _invitations;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly TimeProvider _clock;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;

    public InvitationsModel(
        ApplicationDbContext db,
        InvitationService invitations,
        UserManager<ApplicationUser> userManager,
        TimeProvider clock,
        IEmailService email,
        IConfiguration configuration)
    {
        _db = db;
        _invitations = invitations;
        _userManager = userManager;
        _clock = clock;
        _email = email;
        _configuration = configuration;
    }

    public bool EmailEnabled => _email.IsConfigured;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<Invitation> Invitations { get; private set; } = [];

    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// Carried in TempData rather than the database: the token is never stored in
    /// the clear, so this is the only moment the link exists.
    /// </summary>
    public string? IssuedLink => TempData["IssuedLink"] as string;

    public string? IssuedEmail => TempData["IssuedEmail"] as string;

    public class InputModel
    {
        [Required]
        [EmailAddress]
        [StringLength(200)]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Role { get; set; } = Roles.Member;

        [Range(1, 30)]
        [Display(Name = "Valid for (days)")]
        public int ValidForDays { get; set; } = 7;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostIssueAsync()
    {
        await LoadAsync();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!Roles.All.Contains(Input.Role))
        {
            ModelState.AddModelError(nameof(Input.Role), "Pick a valid role.");
            return Page();
        }

        var normalised = InvitationService.Normalise(Input.Email);
        if (await _db.Users.AnyAsync(u => u.NormalizedEmail == normalised))
        {
            ModelState.AddModelError(nameof(Input.Email), "That address already belongs to a member.");
            return Page();
        }

        var issued = await _invitations.IssueAsync(
            Input.Email, Input.Role, Input.ValidForDays, _userManager.GetUserId(User)!);

        var link = Url.Page("/Account/Register", pageHandler: null,
            values: new { token = issued.Token }, protocol: Request.Scheme);

        TempData["IssuedLink"] = link;
        TempData["IssuedEmail"] = Input.Email.Trim();

        var emailed = false;
        if (_email.IsConfigured)
        {
            var portal = _configuration["Portal:Name"] ?? "The Tool Shed";
            emailed = await _email.SendAsync(Input.Email.Trim(), $"You are invited to {portal}",
                $"You have been invited to join {portal}, a members-only place to lend and borrow tools.\n\n" +
                $"Create your account here (the link works once, until {issued.Invitation.ExpiresUtc.ToLocalTime():d MMM yyyy}):\n{link}");
        }

        TempData["Status"] = emailed ? "Invitation created and emailed." : "Invitation created.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(int invitationId)
    {
        var revoked = await _invitations.RevokeAsync(invitationId);
        TempData[revoked ? "Status" : "Error"] =
            revoked ? "Invitation revoked." : "That invitation could not be revoked.";
        return RedirectToPage();
    }

    public string Describe(Invitation invitation)
    {
        if (invitation.IsRedeemed)
        {
            return $"Accepted {invitation.RedeemedUtc!.Value.ToLocalTime():d MMM yyyy}";
        }

        if (invitation.IsRevoked)
        {
            return "Revoked";
        }

        return invitation.IsExpiredAt(Now) ? "Expired" : "Waiting";
    }

    private async Task LoadAsync() =>
        Invitations = await _db.Invitations.AsNoTracking()
            .OrderByDescending(i => i.CreatedUtc)
            .Take(100)
            .ToListAsync();
}
