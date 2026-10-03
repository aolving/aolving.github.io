namespace ToolShed.Web.Models;

/// <summary>
/// A sign-in for one phone. Only the SHA-256 hash of the token is stored, so a leaked database
/// cannot be replayed against the API. The token also remembers the member's security stamp:
/// a password change, role change or suspension rolls the stamp and ends every device at once.
/// </summary>
public class ApiToken
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string SecurityStamp { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset LastUsedUtc { get; set; }

    public DateTimeOffset ExpiresUtc { get; set; }

    public DateTimeOffset? RevokedUtc { get; set; }
}
