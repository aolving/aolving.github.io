using ToolShed.Client;
using ToolShed.Contracts;

namespace ToolShed.Mobile.Services;

public enum RestoreResult
{
    NotSignedIn,
    SignedIn,

    /// <summary>A sign-in is saved, but the portal could not be reached to confirm it.</summary>
    Unreachable
}

/// <summary>
/// Who is signed in, to which portal. The portal address is a preference; the device token lives in
/// the platform's secure storage (Keychain on iOS, encrypted preferences on Android).
/// </summary>
public sealed class PortalSession
{
    private const string ServerKey = "portal-address";
    private const string TokenKey = "portal-token";

    private HttpClient? _http;
    private PortalClient? _client;

    public PortalClient Client => _client ?? throw new InvalidOperationException("Not connected to a portal.");

    public Uri? Server { get; private set; }

    public UserDto? User { get; private set; }

    /// <summary>The address typed last time, so signing in again is quicker.</summary>
    public string SavedServer => Preferences.Default.Get(ServerKey, string.Empty);

    public static string DeviceName => $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim();

    /// <summary>Raised after the member has signed out, or the portal has ended the sign-in.</summary>
    public event Action? SignedOut;

    public async Task<RestoreResult> RestoreAsync()
    {
        var token = await SecureStorage.Default.GetAsync(TokenKey);
        if (string.IsNullOrEmpty(token) || !ServerAddress.TryNormalise(SavedServer, out var server, out _))
        {
            return RestoreResult.NotSignedIn;
        }

        Connect(server);
        _client!.Token = token;

        try
        {
            User = await _client.GetMeAsync();
            return RestoreResult.SignedIn;
        }
        catch (PortalApiException ex) when (ex.IsUnauthorized)
        {
            await ForgetAsync();
            return RestoreResult.NotSignedIn;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline. Keep the saved sign-in so it works again once the network is back.
            return RestoreResult.Unreachable;
        }
    }

    public async Task SignInAsync(Uri server, string email, string password)
    {
        Connect(server);
        var auth = await _client!.LoginAsync(email, password, DeviceName);
        await RememberAsync(server, auth);
    }

    public async Task RegisterAsync(Uri server, string email, string accessCode, string displayName, string password, string? location)
    {
        Connect(server);
        var auth = await _client!.RegisterAsync(email, accessCode, displayName, password, location, DeviceName);
        await RememberAsync(server, auth);
    }

    public async Task SignOutAsync()
    {
        try
        {
            if (_client?.Token is not null)
            {
                await _client.LogoutAsync();
            }
        }
        catch (Exception ex) when (ex is PortalApiException or HttpRequestException or TaskCanceledException)
        {
            // Signing out must always work locally, even when the portal cannot be reached.
        }

        await ForgetAsync();
        SignedOut?.Invoke();
    }

    private void Connect(Uri server)
    {
        _http?.Dispose();
        _http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        _client = new PortalClient(_http);
        Server = server;
    }

    private async Task RememberAsync(Uri server, AuthResponse auth)
    {
        User = auth.User;
        Preferences.Default.Set(ServerKey, server.ToString());
        await SecureStorage.Default.SetAsync(TokenKey, auth.Token);
    }

    private Task ForgetAsync()
    {
        User = null;
        if (_client is not null)
        {
            _client.Token = null;
        }

        SecureStorage.Default.Remove(TokenKey);
        return Task.CompletedTask;
    }
}
