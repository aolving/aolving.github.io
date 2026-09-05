using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;

namespace ToolShed.Web.Services;

public record InvitationIssued(Invitation Invitation, string Token);

public class InvitationService
{
    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<InvitationService> _logger;

    public InvitationService(ApplicationDbContext db, TimeProvider clock, ILogger<InvitationService> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Issues a single-use invitation. The plain-text token is returned exactly once —
    /// it is never persisted, so it cannot be recovered afterwards.
    /// </summary>
    public async Task<InvitationIssued> IssueAsync(string email, string role, int validForDays, string createdById)
    {
        if (!Roles.All.Contains(role))
        {
            throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        }

        var now = _clock.GetUtcNow();
        var normalised = Normalise(email);

        // Supersede any invitation still outstanding for this address, so re-inviting
        // somebody does not leave two live tokens for one mailbox.
        var outstanding = await _db.Invitations
            .Where(i => i.Email == normalised && i.RedeemedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now)
            .ToListAsync();

        foreach (var stale in outstanding)
        {
            stale.RevokedUtc = now;
        }

        var token = TokenGenerator.NewToken();
        var invitation = new Invitation
        {
            Email = normalised,
            TokenHash = TokenGenerator.Hash(token),
            Role = role,
            CreatedById = createdById,
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(Math.Clamp(validForDays, 1, 30))
        };

        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Invitation {Id} issued for {Email} by {CreatedById}", invitation.Id, normalised, createdById);
        return new InvitationIssued(invitation, token);
    }

    /// <summary>Returns the invitation a token unlocks, or null if it is unknown, spent, revoked or expired.</summary>
    public async Task<Invitation?> FindUsableAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = TokenGenerator.Hash(token.Trim());
        var invitation = await _db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash);
        return invitation is not null && invitation.IsUsableAt(_clock.GetUtcNow()) ? invitation : null;
    }

    /// <summary>Marks the invitation spent. Call inside the same transaction as account creation.</summary>
    public async Task RedeemAsync(Invitation invitation, string userId)
    {
        invitation.RedeemedUtc = _clock.GetUtcNow();
        invitation.RedeemedByUserId = userId;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Invitation {Id} redeemed by {UserId}", invitation.Id, userId);
    }

    public async Task<bool> RevokeAsync(int invitationId)
    {
        var invitation = await _db.Invitations.FindAsync(invitationId);
        if (invitation is null || invitation.IsRedeemed || invitation.IsRevoked)
        {
            return false;
        }

        invitation.RevokedUtc = _clock.GetUtcNow();
        await _db.SaveChangesAsync();
        return true;
    }

    public static string Normalise(string email) => email.Trim().ToUpperInvariant();
}
