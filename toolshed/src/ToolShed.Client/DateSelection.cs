namespace ToolShed.Client;

/// <summary>
/// The rules for picking loan dates on a calendar: the first day you pick is the start, the second is the
/// last day, and you can never go backwards. Days that could not make a valid booking cannot be picked at
/// all, so a member is never offered a choice the portal would then refuse.
///
/// Mirrors the server's booking rules (BookingService.ValidateRange), which stay authoritative. The same
/// rules are implemented for the website (booking-calendar.js) and the demo, with matching tests.
/// </summary>
public sealed class DateSelection
{
    /// <summary>How far ahead a loan may start. Matches the server.</summary>
    public const int HorizonDays = 180;

    private readonly DateOnly _today;
    private readonly int _maxLoanDays;
    private readonly List<(DateOnly Start, DateOnly End)> _held;

    /// <param name="held">Ranges already requested or booked on this tool.</param>
    public DateSelection(DateOnly today, int maxLoanDays, IEnumerable<(DateOnly Start, DateOnly End)> held)
    {
        _today = today;
        _maxLoanDays = Math.Max(1, maxLoanDays);
        _held = held.OrderBy(h => h.Start).ToList();
    }

    /// <summary>The first day of the loan, once picked. Always the earlier of the two dates.</summary>
    public DateOnly? Start { get; private set; }

    /// <summary>The last day of the loan, once picked. Never before <see cref="Start"/>.</summary>
    public DateOnly? End { get; private set; }

    public bool IsComplete => Start is not null && End is not null;

    /// <summary>Days in the loan, counting both ends, so a single day is one day.</summary>
    public int Days => IsComplete ? End!.Value.DayNumber - Start!.Value.DayNumber + 1 : 0;

    public DateOnly Today => _today;

    public DateOnly LastBookableDay => _today.AddDays(HorizonDays);

    public bool IsHeld(DateOnly day) => _held.Any(h => h.Start <= day && day <= h.End);

    /// <summary>
    /// Whether a click on this day would do anything. With no start yet (or a finished selection, which a
    /// click restarts) it must be a free day in the booking window. While choosing the last day it must also
    /// not be before the start, not be longer than the owner allows, and not run into someone else's booking.
    /// </summary>
    public bool CanPick(DateOnly day) =>
        Start is DateOnly start && End is null
            ? CanPickEnd(start, day)
            : CanPickStart(day);

    /// <summary>Picks the day if allowed. Returns whether anything changed.</summary>
    public bool Pick(DateOnly day)
    {
        if (!CanPick(day))
        {
            return false;
        }

        if (Start is not null && End is null)
        {
            End = day;
        }
        else
        {
            // The first date is always the start, including after a finished selection.
            Start = day;
            End = null;
        }

        return true;
    }

    public void Clear()
    {
        Start = null;
        End = null;
    }

    /// <summary>The last day that can be chosen as the end, given the start. Null when no start.</summary>
    public DateOnly? LatestEnd()
    {
        if (Start is not DateOnly start)
        {
            return null;
        }

        var latest = start.AddDays(_maxLoanDays - 1);
        var nextHeld = _held.Where(h => h.Start > start).Select(h => (DateOnly?)h.Start).FirstOrDefault();
        return nextHeld is DateOnly blocker && blocker.AddDays(-1) < latest ? blocker.AddDays(-1) : latest;
    }

    /// <summary>A line telling the member what to do next, or what they have chosen.</summary>
    public string Summary()
    {
        if (Start is not DateOnly start)
        {
            return "Pick the first day of the loan.";
        }

        if (End is not DateOnly end)
        {
            return $"Collect on {Describe(start)}. Now pick the last day. Earlier days cannot be chosen.";
        }

        return start == end
            ? $"{Describe(start)} (1 day)"
            : $"{Describe(start)} to {Describe(end)} ({Days} days)";
    }

    private bool CanPickStart(DateOnly day) =>
        day >= _today && day <= LastBookableDay && !IsHeld(day);

    private bool CanPickEnd(DateOnly start, DateOnly day) =>
        day >= start && day <= (LatestEnd() ?? start) && !IsHeld(day);

    private static string Describe(DateOnly day) => day.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
}
