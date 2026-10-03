using System.Security.Cryptography;
using System.Text;

namespace ToolShed.Web.Services;

public static class AccessCodes
{
    public const int Length = 6;

    /// <summary>A uniformly random code from 000000 to 999999, from the system CSPRNG.</summary>
    public static string Generate() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString($"D{Length}");

    /// <summary>
    /// Accepts what a person types or pastes ("123 456", "123-456", " 123456 ") and returns the six
    /// digits, or false for anything else, so junk never reaches the database.
    /// </summary>
    public static bool TryNormalise(string? input, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var digits = new StringBuilder(Length);
        foreach (var c in input)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (!char.IsWhiteSpace(c) && c != '-')
            {
                return false;
            }
        }

        if (digits.Length != Length)
        {
            return false;
        }

        code = digits.ToString();
        return true;
    }

    /// <summary>Shown to the administrator as two groups of three, which is easier to read out loud.</summary>
    public static string Display(string code) => code.Length == Length ? $"{code[..3]} {code[3..]}" : code;
}

/// <summary>
/// Hashes access codes with a server-held secret. A plain hash would not do: there are only a
/// million six-digit codes, so anyone holding a copy of the database could reverse every one of
/// them in a blink. With the secret kept apart from the database, a stolen copy reveals nothing.
/// </summary>
public sealed class AccessCodeHasher
{
    private readonly byte[] _key;

    public AccessCodeHasher(byte[] key)
    {
        if (key.Length < 16)
        {
            throw new ArgumentException("The access-code key must be at least 16 bytes.", nameof(key));
        }

        _key = key;
    }

    /// <summary>Loads the secret from a file in the given folder, creating it on first use.</summary>
    public static AccessCodeHasher FromDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "access-code.key");

        if (!File.Exists(path))
        {
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(RandomNumberGenerator.GetBytes(32));
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process created it a moment ago; use theirs.
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        return new AccessCodeHasher(File.ReadAllBytes(path));
    }

    public string Hash(string code) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(code)));

    public bool Matches(string expectedHash, string code) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHash), Encoding.ASCII.GetBytes(Hash(code)));
}
