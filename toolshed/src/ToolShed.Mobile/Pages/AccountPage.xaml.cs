using ToolShed.Mobile.Services;

namespace ToolShed.Mobile.Pages;

public partial class AccountPage : PageBase
{
    private readonly PhotoCache _photos;

    public AccountPage(PortalSession session, PhotoCache photos) : base(session)
    {
        InitializeComponent();
        _photos = photos;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        var user = Session.User;
        NameLabel.Text = user?.DisplayName;
        EmailLabel.Text = user?.Email;
        RoleLabel.Text = user is null ? null : user.IsAdmin ? "Administrator" : "Member";
        ServerLabel.Text = Session.Server?.Host;
        AdminHint.IsVisible = user?.IsAdmin == true;
    }

    private async void OnOpenWebsite(object? sender, EventArgs e)
    {
        if (Session.Server is not null)
        {
            await Launcher.Default.OpenAsync(Session.Server);
        }
    }

    private async void OnSignOut(object? sender, EventArgs e)
    {
        if (!await DisplayAlert("Sign out", "Sign out of The Tool Shed on this device?", "Sign out", "Stay"))
        {
            return;
        }

        _photos.Clear();
        await Session.SignOutAsync();
    }
}
