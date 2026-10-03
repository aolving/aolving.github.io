using ToolShed.Contracts;
using ToolShed.Mobile.Services;
using ToolShed.Mobile.ViewModels;

namespace ToolShed.Mobile.Pages;

[QueryProperty(nameof(ToolIdText), "id")]
public partial class ToolDetailPage : PageBase
{
    /// <summary>How many days the availability strip covers. Matches the website.</summary>
    private const int StripDays = 42;

    private readonly PhotoCache _photos;
    private int _toolId;
    private ToolDetailDto? _tool;

    public ToolDetailPage(PortalSession session, PhotoCache photos) : base(session)
    {
        InitializeComponent();
        _photos = photos;

        var today = DateTime.Today;
        StartPicker.MinimumDate = today;
        EndPicker.MinimumDate = today;
        StartPicker.Date = today.AddDays(1);
        EndPicker.Date = today.AddDays(2);
        StartPicker.DateSelected += (_, e) =>
        {
            // Keep the return date from falling behind the collection date.
            if (EndPicker.Date < e.NewDate)
            {
                EndPicker.Date = e.NewDate;
            }

            EndPicker.MinimumDate = e.NewDate;
        };
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

        BuildStrip(tool);

        var photos = tool.Photos.Select(p => new PhotoItem(p)).ToList();
        Photos.ItemsSource = photos;
        Photos.IsVisible = photos.Count > 0;
        foreach (var item in photos)
        {
            _ = LoadPhotoAsync(item);
        }
    }

    private async Task LoadPhotoAsync(PhotoItem item) => item.Image = await _photos.GetAsync(item.Id);

    /// <summary>The same six-week calendar the website shows, with weeks starting on Monday.</summary>
    private void BuildStrip(ToolDetailDto tool)
    {
        StripGrid.Children.Clear();
        StripGrid.ColumnDefinitions.Clear();
        StripGrid.RowDefinitions.Clear();

        for (var column = 0; column < 7; column++)
        {
            StripGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var leading = ((int)today.DayOfWeek + 6) % 7;
        var rows = (leading + StripDays + 6) / 7;
        for (var row = 0; row < rows; row++)
        {
            StripGrid.RowDefinitions.Add(new RowDefinition(new GridLength(40)));
        }

        for (var offset = 0; offset < StripDays; offset++)
        {
            var day = today.AddDays(offset);
            var held = tool.Held.Where(h => h.Start <= day && day <= h.End).ToList();
            var color = held.Any(h => h.Approved)
                ? Color.FromArgb("#9A2F2F")
                : held.Count > 0 ? Color.FromArgb("#8A6D1E") : Color.FromArgb("#2F6B3A");

            var cell = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                Stroke = color,
                StrokeThickness = held.Count > 0 ? 2 : 1,
                Padding = 0,
                Content = new Label
                {
                    Text = day.Day.ToString(),
                    FontSize = 13,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                }
            };

            var slot = leading + offset;
            StripGrid.Add(cell, slot % 7, slot / 7);
        }
    }

    private async void OnRequest(object? sender, EventArgs e)
    {
        RequestButton.IsEnabled = false;
        try
        {
            await RunAsync(async () =>
            {
                await Session.Client.RequestBookingAsync(
                    _toolId,
                    DateOnly.FromDateTime(StartPicker.Date ?? DateTime.Today),
                    DateOnly.FromDateTime(EndPicker.Date ?? DateTime.Today),
                    string.IsNullOrWhiteSpace(NoteEditor.Text) ? null : NoteEditor.Text.Trim());

                NoteEditor.Text = string.Empty;
                await DisplayAlertAsync("Request sent", "The owner will see it under Loans.", "OK");
                await LoadAsync();
            });
        }
        finally
        {
            RequestButton.IsEnabled = true;
        }
    }

    private async void OnEdit(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync($"edittool?id={_toolId}");
}
