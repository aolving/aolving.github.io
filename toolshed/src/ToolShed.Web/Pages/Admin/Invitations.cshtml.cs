using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Pages.Admin;

/// <summary>The access-code generator, and the list of every code that has been issued.</summary>
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

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<Invitation> Invitations { get; private set; } = [];

    /// <summary>
    /// The codes just generated. They are rendered straight into the response to the POST and never
    /// stored in the clear, so this is the one time they can be seen.
    /// </summary>
    public IReadOnlyList<IssuedRow> Results { get; private set; } = [];

    public OpenCodeStatus OpenStatus { get; private set; } = new(0, false);

    public DateTimeOffset Now => _clock.GetUtcNow();

    public bool EmailEnabled => _email.IsConfigured;

    public record IssuedRow(string? Email, string Code, string Role, DateTimeOffset ExpiresUtc, string? Link);

    public class InputModel
    {
        [Display(Name = "Email addresses")]
        public string? Emails { get; set; }

        [Range(0, InvitationService.MaxBatch, ErrorMessage = "A batch can hold up to 50 codes, so ask for between 0 and 50 open codes.")]
        [Display(Name = "Open codes")]
        public int OpenCount { get; set; }

        [Required]
        public string Role { get; set; } = Roles.Member;

        [Range(1, 30)]
        [Display(Name = "Valid for (days)")]
        public int ValidForDays { get; set; } = 7;

        [StringLength(80)]
        public string? Label { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostIssueAsync()
    {
        var failure = await GenerateAsync();
        if (failure is not null)
        {
            return failure;
        }

        var emailed = 0;
        if (_email.IsConfigured)
        {
            // Only links are mailed, never codes: the code is handed over by other means, so a copy of
            // the email alone is not enough to join.
            var portal = _configuration["Portal:Name"] ?? "The Tool Shed";
            foreach (var row in Results.Where(r => r.Email is not null))
            {
                var sent = await _email.SendAsync(row.Email!, $"You are invited to {portal}",
                    $"You have been invited to join {portal}, a members-only place to lend and borrow tools.\n\n" +
                    $"Create your account here (the invitation works until {row.ExpiresUtc.ToLocalTime():d MMM yyyy}):\n{row.Link}\n\n" +
                    "You will also need a six-digit access code. The person who invited you will give it to you separately.");
                if (sent)
                {
                    emailed++;
                }
            }
        }

        TempData["Status"] = emailed > 0
            ? $"{Results.Count} code(s) generated; {emailed} invitation link(s) emailed."
            : $"{Results.Count} code(s) generated.";

        // No redirect: the codes exist only in this response, and must not be cached or replayed.
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostIssueCsvAsync()
    {
        var failure = await GenerateAsync();
        if (failure is not null)
        {
            return failure;
        }

        var rows = new List<string> { CsvWriter.Row("Access code", "For", "Role", "Expires", "Label", "Link") };
        rows.AddRange(Results.Select(r => CsvWriter.Row(
            AccessCodes.Display(r.Code),
            r.Email ?? "Open code (any email)",
            r.Role,
            r.ExpiresUtc.ToLocalTime().ToString("yyyy-MM-dd"),
            Input.Label,
            r.Link)));

        Response.Headers.CacheControl = "no-store";
        return File(CsvWriter.ToBytes(rows), "text/csv; charset=utf-8", "access-codes.csv");
    }

    public async Task<IActionResult> OnPostRevokeAsync(int invitationId)
    {
        var revoked = await _invitations.RevokeAsync(invitationId);
        TempData[revoked ? "Status" : "Error"] =
            revoked ? "Code revoked." : "That code could not be revoked.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResumeAsync()
    {
        await _invitations.ResumeOpenCodesAsync();
        TempData["Status"] = "Open codes are working again.";
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

        if (invitation.IsExpiredAt(Now))
        {
            return "Expired";
        }

        return invitation.IsLockedAt(Now) ? "Paused (wrong codes)" : "Waiting";
    }

    /// <summary>Validates the form and generates the batch. Returns a result to send back, or null on success.</summary>
    private async Task<IActionResult?> GenerateAsync()
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

        var emails = ParseEmails(Input.Emails, out var invalid);
        if (invalid.Count > 0)
        {
            ModelState.AddModelError(nameof(Input.Emails), $"These do not look like email addresses: {string.Join(", ", invalid)}.");
            return Page();
        }

        var total = emails.Count + Input.OpenCount;
        if (total < 1)
        {
            ModelState.AddModelError(string.Empty, "Enter at least one email address, or ask for one or more open codes.");
            return Page();
        }

        if (total > InvitationService.MaxBatch)
        {
            ModelState.AddModelError(string.Empty, $"A batch can hold up to {InvitationService.MaxBatch} codes; this asks for {total}.");
            return Page();
        }

        var normalised = emails.Select(InvitationService.Normalise).ToList();
        var existing = await _db.Users
            .Where(u => u.NormalizedEmail != null && normalised.Contains(u.NormalizedEmail))
            .Select(u => u.Email)
            .ToListAsync();

        if (existing.Count > 0)
        {
            ModelState.AddModelError(nameof(Input.Emails), $"These addresses already belong to members: {string.Join(", ", existing)}.");
            return Page();
        }

        var issued = await _invitations.IssueBatchAsync(
            emails, Input.OpenCount, Input.Role, Input.ValidForDays, _userManager.GetUserId(User)!, Input.Label);

        Results = issued.Select(i => new IssuedRow(
            i.Invitation.Email?.ToLowerInvariant(),
            i.Code,
            i.Invitation.Role,
            i.Invitation.ExpiresUtc,
            // The link names an address but never the code, so the two travel separately.
            i.Invitation.Email is null
                ? null
                : Url.Page("/Account/Register", pageHandler: null, values: new { email = i.Invitation.Email.ToLowerInvariant() }, protocol: Request.Scheme)))
            .ToList();

        await LoadAsync();
        return null;
    }

    /// <summary>Splits a pasted list on new lines, commas and semicolons; trims; drops blanks and repeats.</summary>
    internal static List<string> ParseEmails(string? text, out List<string> invalid)
    {
        invalid = [];
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validator = new EmailAddressAttribute();

        foreach (var raw in (text ?? string.Empty).Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length > 200 || raw.Contains(' ') || !validator.IsValid(raw))
            {
                invalid.Add(raw.Length > 40 ? raw[..40] + "…" : raw);
            }
            else if (seen.Add(raw))
            {
                result.Add(raw);
            }
        }

        return result;
    }

    private async Task LoadAsync()
    {
        Invitations = await _db.Invitations.AsNoTracking()
            .OrderByDescending(i => i.CreatedUtc)
            .Take(200)
            .ToListAsync();
        OpenStatus = await _invitations.OpenCodeStatusAsync();
    }
}
