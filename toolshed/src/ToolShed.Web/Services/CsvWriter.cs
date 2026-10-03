using System.Text;

namespace ToolShed.Web.Services;

public static class CsvWriter
{
    /// <summary>
    /// Quotes a field for CSV, and defuses spreadsheet formulas: a value that starts with =, +, - or @
    /// is run as a formula by Excel and friends, so it is prefixed with an apostrophe to keep it as text.
    /// The fields here include text an administrator typed, so this matters.
    /// </summary>
    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0]))
        {
            text = "'" + text;
        }

        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    public static string Row(params string?[] fields) => string.Join(",", fields.Select(Escape));

    /// <summary>
    /// UTF-8 with a byte-order mark, which Excel needs to read accented characters correctly. GetBytes never
    /// writes the mark itself, so it is added from the encoding's preamble.
    /// </summary>
    public static byte[] ToBytes(IEnumerable<string> rows)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(string.Join("\r\n", rows) + "\r\n")];
    }
}
