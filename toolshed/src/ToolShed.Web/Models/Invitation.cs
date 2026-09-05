using System.ComponentModel.DataAnnotations;

namespace ToolShed.Web.Models;

/// <summary>
/// A single-use, expiring, email-bound invitation. Only the SHA-256 hash of the
/// token is stored, so a database leak does not hand out portal access.
/// </summary>
public class Invitation
{
    public int Id { get; set; }

    /// <summary>Normalised (upper-case) email the invite was issued to.</summary>
    [Required]
    public string Email { get; set; } = string.Empty;

    /// <summary>Hex-encoded SHA-256 of the invitation token.</summary>
    [Required]
    public string TokenHash { get; set; } = string.Empty;

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

    public bool IsRedeemed => RedeemedUtc is not null;

    public bool IsRevoked => RevokedUtc is not null;

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresUtc <= now;

    public bool IsUsableAt(DateTimeOffset now) => !IsRedeemed && !IsRevoked && !IsExpiredAt(now);
}
