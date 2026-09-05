using ToolShed.Web.Models;

namespace ToolShed.Web.Services;

public class PhotoStorageOptions
{
    /// <summary>Directory the photo bytes live in. Kept outside wwwroot so nothing is served statically.</summary>
    public string Root { get; set; } = "App_Data/photos";
}

public class PhotoStorage
{
    private readonly string _root;
    private readonly ILogger<PhotoStorage> _logger;

    public PhotoStorage(IWebHostEnvironment environment, IConfiguration configuration, ILogger<PhotoStorage> logger)
    {
        var configured = configuration["Storage:PhotoRoot"] ?? new PhotoStorageOptions().Root;
        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
        _logger = logger;

        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Validates and stores an upload. The caller's file name is discarded; the
    /// stored name is a GUID plus the extension implied by the sniffed content type.
    /// </summary>
    public async Task<(ToolPhoto? Photo, string? Error)> SaveAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        await using var upload = file.OpenReadStream();
        var inspection = ImageValidator.Inspect(upload, file.Length);
        if (!inspection.IsValid)
        {
            return (null, inspection.Error);
        }

        var storedName = $"{Guid.NewGuid():N}{inspection.Extension}";
        var path = Path.Combine(_root, storedName);

        await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await upload.CopyToAsync(destination, cancellationToken);
        }

        return (new ToolPhoto
        {
            StoredFileName = storedName,
            ContentType = inspection.ContentType!,
            ByteSize = file.Length
        }, null);
    }

    public Stream? OpenRead(ToolPhoto photo)
    {
        var path = ResolvePath(photo.StoredFileName);
        return path is not null && File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    public void Delete(ToolPhoto photo)
    {
        var path = ResolvePath(photo.StoredFileName);
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete photo file {File}", photo.StoredFileName);
        }
    }

    /// <summary>Re-checks that the stored name is a bare file name inside the photo root.</summary>
    private string? ResolvePath(string storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName) ||
            storedFileName != Path.GetFileName(storedFileName))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(_root, storedFileName));
        var root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
    }
}
