using ToolShed.Mobile.Pages;
using ToolShed.Mobile.Services;

namespace ToolShed.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private readonly PortalSession _session;

    public App(IServiceProvider services, PortalSession session)
    {
        InitializeComponent();

        _services = services;
        _session = session;

        // Whenever the portal ends the sign-in (or the member signs out), go back to the start.
        _session.SignedOut += () => MainThread.BeginInvokeOnMainThread(() => ShowWelcome());

        MainPage = new ContentPage
        {
            Content = new ActivityIndicator { IsRunning = true, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center }
        };

        _ = StartAsync();
    }

    public void ShowMain() => MainPage = _services.GetRequiredService<AppShell>();

    public void ShowWelcome(string? notice = null)
    {
        var welcome = _services.GetRequiredService<WelcomePage>();
        welcome.Notice = notice;
        MainPage = new NavigationPage(welcome) { BarBackgroundColor = Color.FromArgb("#8A4B1E"), BarTextColor = Colors.White };
    }

    private async Task StartAsync()
    {
        switch (await _session.RestoreAsync())
        {
            case RestoreResult.SignedIn:
                ShowMain();
                break;
            case RestoreResult.Unreachable:
                ShowWelcome("Could not reach the portal. Check your connection, then sign in again.");
                break;
            default:
                ShowWelcome();
                break;
        }
    }
}
