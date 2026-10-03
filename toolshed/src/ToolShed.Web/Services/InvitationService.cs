using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;

namespace ToolShed.Web.Services;

public record InvitationIssued(Invitation Invitation, string Code);

public class InvitationService
{
    /// <summary>
    /// Shown for every failure alike (no invitation for that address, wrong code, spent, expired,
    /// locked), so the form cannot be used to find out who has been invited.
    /// </summary>
    public const string NotValidMessage =
        "That email and access code do not match a valid invitation. Check both, or ask your administrator for a new one.";

    /// <summary>After this many wrong codes in a row, checking pauses for <see cref="LockFor"/>.</summary>
    public const int LockAfterFailures = 5;

    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    /// <summary>Past this many wrong codes in total the invitation is cancelled and must be reissued.</summary>
    public const int MaxTotalFailures = 30;

    private const int MaxCodeAttempts = 50;

    private readonly ApplicationDbContext _db;
    private readonly AccessCodeHasher _hasher;
    private readonly TimeProvider _clock;
    private readonly ILogger<InvitationService> _logger;

    public InvitationService(ApplicationDbContext db, AccessCodeHasher hasher, TimeProvider clock, ILogger<InvitationService> logger)
    {
        _db = db;
        _hasher = hasher;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Issues a single-use invitation with a fresh six-digit access code that no other invitation has
    /// ever had. The code is returned exactly once; only its keyed hash is kept.
    /// </summary>
    public async Task<InvitationIssued> IssueAsync(string email, string role, int validForDays, string createdById)
    {
        if (!Roles.All.Contains(role))
        {
            throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        }

        var now = _clock.GetUtcNow();
        var normalised = Normalise(email);

        // Supersede any invitation still outstanding for this address, so re-inviting somebody does
        // not leave two live codes for one mailbox.
        var outstanding = await _db.Invitations
            .Where(i => i.Email == normalised && i.RedeemedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now)
            .ToListAsync();

        foreach (var stale in outstanding)
        {
            stale.RevokedUtc = now;
        }

        var (code, hash) = await NewUniqueCodeAsync();
        var invitation = new Invitation
        {
            Email = normalised,
            CodeHash = hash,
            Role = role,
            CreatedById = createdById,
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(Math.Clamp(validForDays, 1, 30))
        };

        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Invitation {Id} issued for {Email} by {CreatedById}", invitation.Id, normalised, createdById);
        return new InvitationIssued(invitation, code);
    }

    /// <summary>
    /// Checks an email and access code. Returns the invitation they unlock, or null, with no hint as to
    /// why. A wrong code counts against the invitation: five in a row pause checking for fifteen
    /// minutes, and thirty cancel it, which caps the odds of guessing a code at 30 in a million.
    /// </summary>
    public async Task<Invitation?> VerifyAsync(string? email, string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(email) || !AccessCodes.TryNormalise(rawCode, out var code))
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        var normalised = Normalise(email);

        var invitation = await _db.Invitations
            .Where(i => i.Email == normalised && i.RedeemedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now)
            .OrderByDescending(i => i.CreatedUtc)
            .FirstOrDefaultAsync();

        if (invitation is null || invitation.IsLockedAt(now))
        {
            return null;
        }

        if (_hasher.Matches(invitation.CodeHash, code))
        {
            return invitation;
        }

        invitation.FailedAttempts++;
        if (invitation.FailedAttempts >= MaxTotalFailures)
        {
            invitation.RevokedUtc = now;
            _logger.LogWarning("Invitation {Id} cancelled after {Count} wrong access codes", invitation.Id, invitation.FailedAttempts);
        }
        else if (invitation.FailedAttempts % LockAfterFailures == 0)
        {
            invitation.LockedUntilUtc = now.Add(LockFor);
            _logger.LogWarning("Invitation {Id} paused after {Count} wrong access codes", invitation.Id, invitation.FailedAttempts);
        }

        await _db.SaveChangesAsync();
        return null;
    }

    /// <summary>Marks the invitation spent. Call after the account has been created.</summary>
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

    /// <summary>A code no invitation, spent or not, has used, so each code identifies exactly one invitation.</summary>
    private async Task<(string Code, string Hash)> NewUniqueCodeAsync()
    {
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = AccessCodes.Generate();
            var hash = _hasher.Hash(code);
            if (!await _db.Invitations.AnyAsync(i => i.CodeHash == hash))
            {
                return (code, hash);
            }
        }

        throw new InvalidOperationException("Could not find an unused access code. Too many invitations have been issued.");
    }
}
