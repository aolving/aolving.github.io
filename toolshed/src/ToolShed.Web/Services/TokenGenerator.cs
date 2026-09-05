using System.Security.Cryptography;

namespace ToolShed.Web.Services;

public static class TokenGenerator
{
    /// <summary>256 bits of CSPRNG output, base64url encoded so it survives a URL and an email client.</summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>Hex SHA-256. Invitation tokens are stored hashed and looked up by hash.</summary>
    public static string Hash(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>A throwaway bootstrap password that satisfies the portal's password policy.</summary>
    public static string NewPassword()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return "Aa1!" + Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
