using ToolShed.Contracts;
using ToolShed.Mobile.Services;
using ToolShed.Mobile.ViewModels;

namespace ToolShed.Mobile.Pages;

public partial class LoansPage : PageBase
{
    private BookingsDto? _bookings;
    private bool _showingIncoming = true;

    public LoansPage(PortalSession session) : base(session) => InitializeComponent();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(LoadAsync);
    }

    private async Task LoadAsync()
    {
        _bookings = await Session.Client.GetBookingsAsync();

        // Land on whichever list has something waiting: an owner's pending requests come first.
        if (_bookings.Incoming.Any(b => b.Status == BookingStatuses.Requested))
        {
            _showingIncoming = true;
        }

        ShowList();
    }

    private void ShowList()
    {
        var source = _showingIncoming ? _bookings?.Incoming : _bookings?.Outgoing;
        List.ItemsSource = (source ?? []).Select(b => new BookingItem(b)).ToList();
        EmptyLabel.Text = _showingIncoming ? "Nobody has asked to borrow anything." : "You have not requested anything.";

        // The active tab is the solid one; the other is quieter.
        IncomingButton.Opacity = _showingIncoming ? 1 : 0.55;
        OutgoingButton.Opacity = _showingIncoming ? 0.55 : 1;
    }

    private void OnShowIncoming(object? sender, EventArgs e)
    {
        _showingIncoming = true;
        ShowList();
    }

    private void OnShowOutgoing(object? sender, EventArgs e)
    {
        _showingIncoming = false;
        ShowList();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        try
        {
            await RunAsync(LoadAsync);
        }
        finally
        {
            Refresh.IsRefreshing = false;
        }
    }

    private static BookingItem? ItemOf(object? sender) => (sender as BindableObject)?.BindingContext as BookingItem;

    private async void OnApprove(object? sender, EventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        var note = await DisplayPromptAsync("Approve", "Add a note for the borrower (optional).", "Approve", "Back", maxLength: 1000);
        if (note is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Session.Client.ApproveAsync(item.Id, note);
            await LoadAsync();
        });
    }

    private async void OnDecline(object? sender, EventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        var note = await DisplayPromptAsync("Decline", "Tell the borrower why (optional).", "Decline", "Back", maxLength: 1000);
        if (note is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Session.Client.DeclineAsync(item.Id, note);
            await LoadAsync();
        });
    }

    private async void OnReturned(object? sender, EventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Session.Client.MarkReturnedAsync(item.Id);
            await LoadAsync();
        });
    }

    private async void OnCancel(object? sender, EventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        if (!await DisplayAlertAsync("Cancel", "Call this loan off? The dates become free again.", "Cancel loan", "Keep"))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Session.Client.CancelAsync(item.Id);
            await LoadAsync();
        });
    }
}
