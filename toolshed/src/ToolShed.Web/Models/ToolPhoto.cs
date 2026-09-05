namespace ToolShed.Web.Models;

/// <summary>
/// Metadata for an uploaded photo. The bytes live outside wwwroot and are only
/// ever served to signed-in members through the /photos/{id} endpoint.
/// </summary>
public class ToolPhoto
{
    public int Id { get; set; }

    public int ToolId { get; set; }

    public Tool? Tool { get; set; }

    /// <summary>Server-generated name on disk. The uploaded file name is never trusted or reused.</summary>
    public string StoredFileName { get; set; } = string.Empty;

    /// <summary>Sniffed from the file's magic bytes, not from the browser-supplied header.</summary>
    public string ContentType { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    public string? Caption { get; set; }

    public bool IsPrimary { get; set; }

    public DateTimeOffset UploadedUtc { get; set; } = DateTimeOffset.UtcNow;
}
