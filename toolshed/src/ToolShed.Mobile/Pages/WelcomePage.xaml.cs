using ToolShed.Client;
using ToolShed.Mobile.Services;

namespace ToolShed.Mobile.Pages;

public partial class WelcomePage : PageBase
{
    private readonly IServiceProvider _services;
    private string? _notice;

    public WelcomePage(PortalSession session, IServiceProvider services) : base(session)
    {
        InitializeComponent();
        _services = services;
        ServerEntry.Text = session.SavedServer;
    }

    /// <summary>A line shown above the form, for example why the member was signed out.</summary>
    public string? Notice
    {
        get => _notice;
        set
        {
            _notice = value;
            NoticeLabel.Text = value;
            NoticeLabel.IsVisible = !string.IsNullOrEmpty(value);
        }
    }

    private async void OnSignIn(object? sender, EventArgs e)
    {
        if (!ServerAddress.TryNormalise(ServerEntry.Text, out var server, out var problem))
        {
            await DisplayAlertAsync("Portal address", problem, "OK");
            return;
        }

        if (string.IsNullOrWhiteSpace(EmailEntry.Text) || string.IsNullOrEmpty(PasswordEntry.Text))
        {
            await DisplayAlertAsync("Sign in", "Enter your email and password.", "OK");
            return;
        }

        SignInButton.IsEnabled = false;
        try
        {
            await RunAsync(async () =>
            {
                await Session.SignInAsync(server, EmailEntry.Text.Trim(), PasswordEntry.Text);
                PasswordEntry.Text = string.Empty;
                ((App)Application.Current!).ShowMain();
            });
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private async void OnInvitation(object? sender, EventArgs e) =>
        await Navigation.PushAsync(_services.GetRequiredService<InvitePage>().WithServer(ServerEntry.Text));
}
