using System.ComponentModel.DataAnnotations;

namespace ToolShed.Web.Models;

/// <summary>
/// A single-use, expiring invitation for one email address. Joining needs that address plus the
/// six-digit access code issued with the invitation. The code is stored only as a keyed hash.
/// </summary>
public class Invitation
{
    public int Id { get; set; }

    /// <summary>Normalised (upper-case) email the invite was issued to.</summary>
    [Required]
    public string Email { get; set; } = string.Empty;

    /// <summary>Keyed hash (HMAC-SHA256, hex) of the six-digit access code. Unique across all invitations.</summary>
    [Required]
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Role granted on redemption. Restricted to <see cref="Roles"/>.</summary>
    [Required]
    public string Role { get; set; } = Roles.Member;

    public string? CreatedById { get; set; }

    public ApplicationUser? CreatedBy { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresUtc { get; set; }

    public DateTimeOffset? RedeemedUtc { get; set; }

    public string? RedeemedByUserId { get; set; }

    public DateTimeOffset? RevokedUtc { get; set; }

    /// <summary>Wrong codes tried against this invitation. Slows guessing, and ends it past a limit.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>While set and in the future, codes are not even checked.</summary>
    public DateTimeOffset? LockedUntilUtc { get; set; }

    public bool IsRedeemed => RedeemedUtc is not null;

    public bool IsRevoked => RevokedUtc is not null;

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresUtc <= now;

    public bool IsLockedAt(DateTimeOffset now) => LockedUntilUtc is DateTimeOffset until && until > now;

    public bool IsUsableAt(DateTimeOffset now) => !IsRedeemed && !IsRevoked && !IsExpiredAt(now);
}
