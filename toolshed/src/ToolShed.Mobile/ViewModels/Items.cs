using ToolShed.Contracts;

namespace ToolShed.Mobile.ViewModels;

public sealed class ToolItem : Observable
{
    private ImageSource? _cover;

    public ToolItem(ToolSummaryDto tool)
    {
        Tool = tool;
        Subtitle = tool.IsMine ? $"{tool.Category} · yours" : $"{tool.Category} · {tool.OwnerName}";

        if (!tool.IsListed)
        {
            Status = "Paused";
            StatusColor = Color.FromArgb("#9A2F2F");
        }
        else if (tool.OnLoanUntil is DateOnly until)
        {
            Status = $"On loan until {until:d MMM}";
            StatusColor = Color.FromArgb("#9A2F2F");
        }
        else
        {
            Status = "Available";
            StatusColor = Color.FromArgb("#2F6B3A");
        }
    }

    public ToolSummaryDto Tool { get; }

    public int Id => Tool.Id;

    public string Name => Tool.Name;

    public string Subtitle { get; }

    public string Status { get; }

    public Color StatusColor { get; }

    public ImageSource? Cover
    {
        get => _cover;
        set => Set(ref _cover, value);
    }
}

public sealed class PhotoItem : Observable
{
    private ImageSource? _image;
    private bool _isCover;

    public PhotoItem(PhotoDto photo)
    {
        Photo = photo;
        _isCover = photo.IsCover;
    }

    public PhotoDto Photo { get; }

    public int Id => Photo.Id;

    public bool IsCover
    {
        get => _isCover;
        set
        {
            if (Set(ref _isCover, value))
            {
                // The "make cover" button is only offered for photos that are not already the cover.
                Raise(nameof(CanMakeCover));
            }
        }
    }

    public bool CanMakeCover => !_isCover;

    public ImageSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }
}

public sealed class BookingItem
{
    public BookingItem(BookingDto booking)
    {
        Booking = booking;

        Title = booking.IAmOwner
            ? $"{booking.ToolName} for {booking.OtherPartyName}"
            : $"{booking.ToolName} from {booking.OtherPartyName}";

        Dates = booking.StartDate == booking.EndDate
            ? $"{booking.StartDate:ddd d MMM yyyy}"
            : $"{booking.StartDate:d MMM} to {booking.EndDate:d MMM yyyy}";

        var holdsDates = booking.Status is BookingStatuses.Requested or BookingStatuses.Approved;
        CanDecide = booking.IAmOwner && booking.Status == BookingStatuses.Requested;
        CanMarkReturned = booking.IAmOwner && booking.Status == BookingStatuses.Approved;
        CanCancel = holdsDates && !CanDecide;

        StatusText = booking.IsOverdue ? $"{booking.Status} · overdue" : booking.Status;
        StatusColor = booking.IsOverdue || booking.Status is BookingStatuses.Declined or BookingStatuses.Cancelled
            ? Color.FromArgb("#9A2F2F")
            : booking.Status == BookingStatuses.Approved
                ? Color.FromArgb("#2F6B3A")
                : booking.Status == BookingStatuses.Requested
                    ? Color.FromArgb("#8A6D1E")
                    : Color.FromArgb("#7A7368");

        Note = booking.IAmOwner ? booking.BorrowerNote : booking.OwnerNote;
        HasNote = !string.IsNullOrWhiteSpace(Note);
    }

    public BookingDto Booking { get; }

    public int Id => Booking.Id;

    public string Title { get; }

    public string Dates { get; }

    public string StatusText { get; }

    public Color StatusColor { get; }

    public string? Note { get; }

    public bool HasNote { get; }

    /// <summary>The owner can approve or decline a request that is waiting.</summary>
    public bool CanDecide { get; }

    public bool CanMarkReturned { get; }

    /// <summary>Either party can call off a loan that has been approved.</summary>
    public bool CanCancel { get; }
}
