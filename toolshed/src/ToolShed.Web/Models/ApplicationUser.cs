using Microsoft.AspNetCore.Identity;

namespace ToolShed.Web.Models;

/// <summary>
/// A member of the portal. Accounts only ever come into existence by redeeming
/// an <see cref="Invitation"/>; there is no open sign-up path.
/// </summary>
public class ApplicationUser : IdentityUser
{
    [PersonalData]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Free-text neighbourhood/area so members know how far a tool is.</summary>
    [PersonalData]
    public string? Location { get; set; }

    public DateTimeOffset JoinedUtc { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Tool> Tools { get; set; } = new List<Tool>();

    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Member = "Member";

    public static readonly string[] All = [Admin, Member];
}
