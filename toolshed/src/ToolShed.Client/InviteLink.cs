namespace ToolShed.Client;

public static class InviteLink
{
    /// <summary>
    /// Accepts what a member is most likely to paste: the whole invitation link, or just the token.
    /// Returns null for anything else, so the app can say so instead of sending junk to the server.
    /// </summary>
    public static string? ExtractToken(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals("token", StringComparison.OrdinalIgnoreCase))
                {
                    var token = Uri.UnescapeDataString(parts[1]);
                    return IsTokenShaped(token) ? token : null;
                }
            }

            return null;
        }

        return IsTokenShaped(text) ? text : null;
    }

    private static bool IsTokenShaped(string value) =>
        value.Length >= 20 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
