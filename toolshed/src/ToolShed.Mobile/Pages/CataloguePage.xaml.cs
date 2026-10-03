using ToolShed.Mobile.Services;
using ToolShed.Mobile.ViewModels;

namespace ToolShed.Mobile.Pages;

public partial class CataloguePage : PageBase
{
    private readonly PhotoCache _photos;

    public CataloguePage(PortalSession session, PhotoCache photos) : base(session)
    {
        InitializeComponent();
        _photos = photos;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(LoadAsync);
    }

    private async Task LoadAsync()
    {
        var tools = await Session.Client.ListToolsAsync(Search.Text);
        var items = tools.Select(t => new ToolItem(t)).ToList();
        List.ItemsSource = items;

        foreach (var item in items.Where(i => i.Tool.CoverPhotoId is not null))
        {
            _ = LoadCoverAsync(item);
        }
    }

    private async Task LoadCoverAsync(ToolItem item) =>
        item.Cover = await _photos.GetAsync(item.Tool.CoverPhotoId!.Value);

    private async void OnSearch(object? sender, EventArgs e) => await RunAsync(LoadAsync);

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

    private async void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is ToolItem item)
        {
            List.SelectedItem = null;
            await Shell.Current.GoToAsync($"tool?id={item.Id}");
        }
    }
}
