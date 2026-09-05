using System.ComponentModel.DataAnnotations;

namespace ToolShed.Web.Models;

public class Tool
{
    public int Id { get; set; }

    [Required]
    public string OwnerId { get; set; } = string.Empty;

    public ApplicationUser? Owner { get; set; }

    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(60)]
    public string Category { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>Where the borrower collects it, e.g. "Garage, Sellwood".</summary>
    [StringLength(120)]
    public string? PickupLocation { get; set; }

    /// <summary>Owner's cap on a single loan. Bookings longer than this are rejected.</summary>
    [Range(1, 90)]
    public int MaxLoanDays { get; set; } = 14;

    /// <summary>Owner can park a tool (being repaired, lent long term) without deleting it.</summary>
    public bool IsListed { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ToolPhoto> Photos { get; set; } = new List<ToolPhoto>();

    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
