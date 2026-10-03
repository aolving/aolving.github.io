using System.Collections.ObjectModel;
using ToolShed.Contracts;
using ToolShed.Mobile.Services;
using ToolShed.Mobile.ViewModels;

namespace ToolShed.Mobile.Pages;

[QueryProperty(nameof(ToolIdText), "id")]
public partial class EditToolPage : PageBase
{
    /// <summary>Mirrors the server's limit so the app can say so before uploading.</summary>
    private const int MaxPhotos = 6;

    private readonly PhotoCache _photos;
    private readonly ObservableCollection<PhotoItem> _items = new();
    private int? _toolId;

    public EditToolPage(PortalSession session, PhotoCache photos) : base(session)
    {
        InitializeComponent();
        _photos = photos;
        PhotoList.ItemsSource = _items;
        OnDaysChanged(null, new ValueChangedEventArgs(0, DaysStepper.Value));
        UpdatePhotoHint();
    }

    public string? ToolIdText
    {
        set => _toolId = int.TryParse(value, out var id) ? id : null;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_toolId is not null)
        {
            await RunAsync(LoadAsync);
        }
    }

    private async Task LoadAsync()
    {
        var tool = await Session.Client.GetToolAsync(_toolId!.Value);

        Title = "Edit tool";
        NameEntry.Text = tool.Name;
        CategoryEntry.Text = tool.Category;
        DescriptionEditor.Text = tool.Description;
        PickupEntry.Text = tool.PickupLocation;
        DaysStepper.Value = tool.MaxLoanDays;
        ListedSwitch.IsToggled = tool.IsListed;
        DeleteButton.IsVisible = true;

        ShowPhotos(tool.Photos);
    }

    private void ShowPhotos(IEnumerable<PhotoDto> photos)
    {
        _items.Clear();
        foreach (var photo in photos)
        {
            var item = new PhotoItem(photo);
            _items.Add(item);
            _ = LoadImageAsync(item);
        }

        UpdatePhotoHint();
    }

    private async Task LoadImageAsync(PhotoItem item) => item.Image = await _photos.GetAsync(item.Id);

    private void UpdatePhotoHint() =>
        PhotoHint.Text = $"{_items.Count} of {MaxPhotos} photos. The first photo you add becomes the cover.";

    private void OnDaysChanged(object? sender, ValueChangedEventArgs e) =>
        DaysLabel.Text = $"{(int)e.NewValue} day(s)";

    private ToolInput? ReadForm()
    {
        if (string.IsNullOrWhiteSpace(NameEntry.Text) || NameEntry.Text.Trim().Length < 2)
        {
            _ = DisplayAlert("Name", "Give the tool a name.", "OK");
            return null;
        }

        if (string.IsNullOrWhiteSpace(CategoryEntry.Text) || CategoryEntry.Text.Trim().Length < 2)
        {
            _ = DisplayAlert("Category", "Enter a category so people can find it.", "OK");
            return null;
        }

        return new ToolInput(
            NameEntry.Text.Trim(),
            CategoryEntry.Text.Trim(),
            DescriptionEditor.Text,
            PickupEntry.Text,
            (int)DaysStepper.Value,
            ListedSwitch.IsToggled);
    }

    /// <summary>Saves the listing, creating it first if it is new. Photos need a listing to belong to.</summary>
    private async Task<bool> SaveAsync()
    {
        var input = ReadForm();
        if (input is null)
        {
            return false;
        }

        var saved = _toolId is null
            ? await Session.Client.CreateToolAsync(input)
            : await Session.Client.UpdateToolAsync(_toolId.Value, input);

        _toolId = saved.Id;
        Title = "Edit tool";
        DeleteButton.IsVisible = true;
        return true;
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            await RunAsync(async () =>
            {
                if (await SaveAsync())
                {
                    await Shell.Current.GoToAsync("..");
                }
            });
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnTakePhoto(object? sender, EventArgs e)
    {
        if (!MediaPicker.Default.IsCaptureSupported)
        {
            await DisplayAlert("Camera", "This device cannot take photos from here. Choose a photo instead.", "OK");
            return;
        }

        FileResult? photo;
        try
        {
            photo = await MediaPicker.Default.CapturePhotoAsync();
        }
        catch (PermissionException)
        {
            await DisplayAlert("Camera", "Allow camera access in Settings to take photos of your tools.", "OK");
            return;
        }

        if (photo is not null)
        {
            await UploadAsync(photo);
        }
    }

    private async void OnPickPhoto(object? sender, EventArgs e)
    {
        FileResult? photo;
        try
        {
            photo = await MediaPicker.Default.PickPhotoAsync();
        }
        catch (PermissionException)
        {
            await DisplayAlert("Photos", "Allow photo access in Settings to choose photos of your tools.", "OK");
            return;
        }

        if (photo is not null)
        {
            await UploadAsync(photo);
        }
    }

    private async Task UploadAsync(FileResult photo)
    {
        if (_items.Count >= MaxPhotos)
        {
            await DisplayAlert("Photos", $"A listing can have up to {MaxPhotos} photos. Delete one to add another.", "OK");
            return;
        }

        await RunAsync(async () =>
        {
            if (!await SaveAsync())
            {
                return;
            }

            await using var stream = await photo.OpenReadAsync();
            await Session.Client.UploadPhotoAsync(_toolId!.Value, stream, photo.FileName, photo.ContentType ?? "image/jpeg");

            var refreshed = await Session.Client.GetToolAsync(_toolId.Value);
            ShowPhotos(refreshed.Photos);
        });
    }

    private async void OnMakeCover(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not PhotoItem item || _toolId is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Session.Client.SetCoverPhotoAsync(_toolId.Value, item.Id);
            foreach (var other in _items)
            {
                other.IsCover = other.Id == item.Id;
            }
        });
    }

    private async void OnDeletePhoto(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not PhotoItem item || _toolId is null)
        {
            return;
        }

        if (!await DisplayAlert("Delete photo", "Remove this photo from the listing?", "Delete", "Keep"))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Session.Client.DeletePhotoAsync(_toolId.Value, item.Id);
            _photos.Forget(item.Id);

            var refreshed = await Session.Client.GetToolAsync(_toolId.Value);
            ShowPhotos(refreshed.Photos);
        });
    }

    private async void OnDelete(object? sender, EventArgs e)
    {
        if (_toolId is null)
        {
            return;
        }

        if (!await DisplayAlert("Delete listing", "This also deletes its photos and its loan history. Continue?", "Delete", "Keep"))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Session.Client.DeleteToolAsync(_toolId.Value);

            // The tool is gone, so neither this screen nor its detail screen has anywhere to stay.
            await Shell.Current.GoToAsync("//mytools");
        });
    }
}
