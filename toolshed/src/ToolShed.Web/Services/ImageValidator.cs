namespace ToolShed.Web.Services;

public record ImageInspection(bool IsValid, string? ContentType, string? Extension, string? Error)
{
    public static ImageInspection Rejected(string error) => new(false, null, null, error);

    public static ImageInspection Accepted(string contentType, string extension) => new(true, contentType, extension, null);
}

/// <summary>
/// Decides whether an upload is a photo, from its bytes rather than its name or
/// its Content-Type header — both of which are attacker-controlled. SVG is not
/// accepted: it is a script-carrying document, not a photo.
/// </summary>
public static class ImageValidator
{
    public const long MaxBytes = 8 * 1024 * 1024;

    public const int MaxPhotosPerTool = 6;

    public static ImageInspection Inspect(Stream stream, long length)
    {
        if (length <= 0)
        {
            return ImageInspection.Rejected("The file is empty.");
        }

        if (length > MaxBytes)
        {
            return ImageInspection.Rejected($"Photos must be {MaxBytes / (1024 * 1024)} MB or smaller.");
        }

        Span<byte> header = stackalloc byte[16];
        var read = ReadExactly(stream, header);
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        if (read < 12)
        {
            return ImageInspection.Rejected("That does not look like an image file.");
        }

        // JPEG: FF D8 FF
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ImageInspection.Accepted("image/jpeg", ".jpg");
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (header[..8].SequenceEqual(png))
        {
            return ImageInspection.Accepted("image/png", ".png");
        }

        // GIF: "GIF87a" / "GIF89a"
        if (header[0] == 'G' && header[1] == 'I' && header[2] == 'F' && header[3] == '8')
        {
            return ImageInspection.Accepted("image/gif", ".gif");
        }

        // WEBP: "RIFF" .... "WEBP"
        if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
            header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
        {
            return ImageInspection.Accepted("image/webp", ".webp");
        }

        return ImageInspection.Rejected("Only JPEG, PNG, GIF and WebP photos are accepted.");
    }

    private static int ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
