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

    private async void OnJoin(object? sender, EventArgs e)
    {
        if (!ServerAddress.TryNormalise(ServerEntry.Text, out var server, out var problem))
        {
            await DisplayAlertAsync("Portal address", problem, "OK");
            return;
        }

        if (string.IsNullOrWhiteSpace(EmailEntry.Text) || !EmailEntry.Text.Contains('@'))
        {
            await DisplayAlertAsync("Email", "Enter the email address you were invited with.", "OK");
            return;
        }

        var code = AccessCode.Normalise(CodeEntry.Text);
        if (code is null)
        {
            await DisplayAlertAsync("Access code", "The access code is six digits. Check what your administrator gave you.", "OK");
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
                await Session.RegisterAsync(server, EmailEntry.Text.Trim(), code, NameEntry.Text.Trim(), PasswordEntry.Text, LocationEntry.Text);
                ((App)Application.Current!).ShowMain();
            });
        }
        finally
        {
            JoinButton.IsEnabled = true;
        }
    }
}
