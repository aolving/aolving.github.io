using ToolShed.Client;
using ToolShed.Mobile.Services;

namespace ToolShed.Mobile.Pages;

public partial class InvitePage : PageBase
{
    public InvitePage(PortalSession session) : base(session) => InitializeComponent();

    public InvitePage WithServer(string? server)
    {
        ServerEntry.Text = server;
        return this;
    }

    /// <summary>
    /// An invitation link already says which portal it belongs to, so fill that in rather than
    /// asking the member to type it a second time.
    /// </summary>
    private void OnLinkChanged(object? sender, TextChangedEventArgs e)
    {
        if (Uri.TryCreate(e.NewTextValue?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            ServerEntry.Text = uri.GetLeftPart(UriPartial.Authority);
        }
    }

    private async void OnJoin(object? sender, EventArgs e)
    {
        var token = InviteLink.ExtractToken(LinkEntry.Text);
        if (token is null)
        {
            await DisplayAlertAsync("Invitation", "That does not look like an invitation link. Paste the whole link you were sent.", "OK");
            return;
        }

        if (!ServerAddress.TryNormalise(ServerEntry.Text, out var server, out var problem))
        {
            await DisplayAlertAsync("Portal address", problem, "OK");
            return;
        }

        if (string.IsNullOrWhiteSpace(NameEntry.Text) || NameEntry.Text.Trim().Length < 2)
        {
            await DisplayAlertAsync("Your name", "Enter the name other members will see.", "OK");
            return;
        }

        if (PasswordEntry.Text != ConfirmEntry.Text)
        {
            await DisplayAlertAsync("Password", "The two passwords do not match.", "OK");
            return;
        }

        JoinButton.IsEnabled = false;
        try
        {
            await RunAsync(async () =>
            {
                await Session.RegisterAsync(server, token, NameEntry.Text.Trim(), PasswordEntry.Text, LocationEntry.Text);
                ((App)Application.Current!).ShowMain();
            });
        }
        finally
        {
            JoinButton.IsEnabled = true;
        }
    }
}
