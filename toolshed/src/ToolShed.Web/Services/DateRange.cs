namespace ToolShed.Web.Services;

/// <summary>
/// Inclusive day range used by loans. Kept free of EF and HTTP so the booking
/// rules can be unit tested on their own.
/// </summary>
public readonly record struct DateRange(DateOnly Start, DateOnly End)
{
    public int Days => End.DayNumber - Start.DayNumber + 1;

    public bool IsWellFormed => End >= Start;

    /// <summary>Two inclusive ranges clash unless one finishes strictly before the other starts.</summary>
    public bool Overlaps(DateRange other) => Start <= other.End && other.Start <= End;

    public bool Contains(DateOnly day) => day >= Start && day <= End;
}
