using ToolShed.Contracts;
using ToolShed.Mobile.Services;
using ToolShed.Mobile.ViewModels;

namespace ToolShed.Mobile.Pages;

[QueryProperty(nameof(ToolIdText), "id")]
public partial class ToolDetailPage : PageBase
{
    private readonly PhotoCache _photos;
    private int _toolId;
    private ToolDetailDto? _tool;

    public ToolDetailPage(PortalSession session, PhotoCache photos) : base(session)
    {
        InitializeComponent();
        _photos = photos;
    }

    public string? ToolIdText
    {
        set => _toolId = int.TryParse(value, out var id) ? id : 0;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(LoadAsync);
    }

    private async Task LoadAsync()
    {
        var tool = await Session.Client.GetToolAsync(_toolId);
        _tool = tool;

        Title = tool.Name;
        NameLabel.Text = tool.Name;
        MetaLabel.Text = tool.IsMine
            ? $"{tool.Category} · yours"
            : $"{tool.Category} · owned by {tool.OwnerName}"
              + (string.IsNullOrWhiteSpace(tool.PickupLocation) ? string.Empty : $" · collect from {tool.PickupLocation}");
        DescriptionLabel.Text = tool.Description;
        DescriptionLabel.IsVisible = !string.IsNullOrWhiteSpace(tool.Description);
        PausedLabel.IsVisible = !tool.IsListed;

        EditButton.IsVisible = tool.IsMine;
        RequestCard.IsVisible = !tool.IsMine && tool.IsListed;
        LimitLabel.Text = $"Loans on this tool run up to {tool.MaxLoanDays} day(s).";

        // Owners (and anyone looking at a paused tool) see what is booked but cannot pick dates. Everyone
        // else picks on the calendar: first tap is the start, second is the end, never backwards.
        var canBook = !tool.IsMine && tool.IsListed;
        DatesHeading.Text = canBook ? "Pick your dates" : "Availability";
        Calendar.Load(DateOnly.FromDateTime(DateTime.Today), tool.MaxLoanDays, tool.Held, readOnly: !canBook);
        RequestButton.IsEnabled = false;

        var photos = tool.Photos.Select(p => new PhotoItem(p)).ToList();
        Photos.ItemsSource = photos;
        Photos.IsVisible = photos.Count > 0;
        foreach (var item in photos)
        {
            _ = LoadPhotoAsync(item);
        }
    }

    private async Task LoadPhotoAsync(PhotoItem item) => item.Image = await _photos.GetAsync(item.Id);

    // The request can only be sent once the calendar holds a complete, valid range.
    private void OnSelectionChanged(object? sender, EventArgs e) =>
        RequestButton.IsEnabled = Calendar.Selection.IsComplete;

    private async void OnRequest(object? sender, EventArgs e)
    {
        RequestButton.IsEnabled = false;
        try
        {
            await RunAsync(async () =>
            {
                var selection = Calendar.Selection;
                if (selection.Start is not DateOnly start || selection.End is not DateOnly end)
                {
                    return;
                }

                await Session.Client.RequestBookingAsync(
                    _toolId,
                    start,
                    end,
                    string.IsNullOrWhiteSpace(NoteEditor.Text) ? null : NoteEditor.Text.Trim());

                NoteEditor.Text = string.Empty;
                await DisplayAlertAsync("Request sent", "The owner will see it under Loans.", "OK");
                await LoadAsync();
            });
        }
        finally
        {
            // After a successful request the page reloads with nothing chosen, so follow the selection
            // rather than switching the button back on.
            RequestButton.IsEnabled = Calendar.Selection.IsComplete;
        }
    }

    private async void OnEdit(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync($"edittool?id={_toolId}");
}
