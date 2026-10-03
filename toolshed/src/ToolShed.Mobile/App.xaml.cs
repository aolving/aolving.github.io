using ToolShed.Mobile.Pages;
using ToolShed.Mobile.Services;

namespace ToolShed.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private readonly PortalSession _session;
    private Window? _window;

    public App(IServiceProvider services, PortalSession session)
    {
        InitializeComponent();

        _services = services;
        _session = session;

        // Whenever the portal ends the sign-in (or the member signs out), go back to the start.
        _session.SignedOut += () => MainThread.BeginInvokeOnMainThread(() => ShowWelcome());
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _window = new Window(new ContentPage
        {
            Content = new ActivityIndicator { IsRunning = true, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center }
        });

        _ = StartAsync();
        return _window;
    }

    public void ShowMain() => SetPage(_services.GetRequiredService<AppShell>());

    public void ShowWelcome(string? notice = null)
    {
        var welcome = _services.GetRequiredService<WelcomePage>();
        welcome.Notice = notice;
        SetPage(new NavigationPage(welcome) { BarBackgroundColor = Color.FromArgb("#8A4B1E"), BarTextColor = Colors.White });
    }

    private void SetPage(Page page)
    {
        if (_window is not null)
        {
            _window.Page = page;
        }
    }

    private async Task StartAsync()
    {
        var result = await _session.RestoreAsync();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (result)
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
        });
    }
}
