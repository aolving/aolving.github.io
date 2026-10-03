namespace ToolShed.Client;

public static class ServerAddress
{
    /// <summary>
    /// Turns what a member typed ("tools.example.com") into the portal's base address. Plain http is
    /// refused except for a machine on the same device or LAN-style test hosts, because sign-in
    /// details and photos must not cross the internet unencrypted.
    /// </summary>
    public static bool TryNormalise(string? input, out Uri address, out string error)
    {
        address = null!;
        error = string.Empty;

        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "Enter the address of your portal, for example tools.example.com.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            error = "That does not look like a web address.";
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && !IsLocalTestHost(parsed.Host))
        {
            error = "The portal must use https. Plain http is only allowed for testing on this device.";
            return false;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            error = "Only web addresses (https://) are supported.";
            return false;
        }

        var path = parsed.AbsolutePath.EndsWith('/') ? parsed.AbsolutePath : parsed.AbsolutePath + "/";
        address = new UriBuilder(parsed) { Path = path, Query = string.Empty, Fragment = string.Empty }.Uri;
        return true;
    }

    // 10.0.2.2 is how an Android emulator reaches the computer it runs on.
    private static bool IsLocalTestHost(string host) =>
        host is "localhost" or "127.0.0.1" or "10.0.2.2" or "::1" or "[::1]";
}
