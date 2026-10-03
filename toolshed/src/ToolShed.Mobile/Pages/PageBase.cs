using ToolShed.Client;
using ToolShed.Mobile.Services;

namespace ToolShed.Mobile.Pages;

/// <summary>
/// Shared behaviour for every screen: turn a failed call into a message a person can act on, and
/// send them back to sign in when the portal says their sign-in has ended.
/// </summary>
public abstract class PageBase : ContentPage
{
    protected PageBase(PortalSession session) => Session = session;

    protected PortalSession Session { get; }

    protected async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (PortalApiException ex) when (ex.IsUnauthorized)
        {
            await Session.SignOutAsync();
            ((App)Application.Current!).ShowWelcome("Your sign-in has ended. Please sign in again.");
        }
        catch (PortalApiException ex)
        {
            await DisplayAlert("The Tool Shed", ex.Message, "OK");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await DisplayAlert("No connection", "Could not reach the portal. Check your connection and try again.", "OK");
        }
    }
}
