namespace ToolShed.Client;

public static class AccessCode
{
    public const int Length = 6;

    /// <summary>
    /// Turns what a person typed or pasted ("123 456", "123-456", " 123456 ") into the six digits, or
    /// returns null for anything else, so the app can say so before sending anything to the server.
    /// </summary>
    public static string? Normalise(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new System.Text.StringBuilder(Length);
        foreach (var c in input)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (!char.IsWhiteSpace(c) && c != '-')
            {
                return null;
            }
        }

        return digits.Length == Length ? digits.ToString() : null;
    }

    /// <summary>Two groups of three, which is easier to read and to say aloud.</summary>
    public static string Format(string code) => code.Length == Length ? $"{code[..3]} {code[3..]}" : code;
}
