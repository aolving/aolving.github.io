using Microsoft.EntityFrameworkCore;
using ToolShed.Contracts;
using ToolShed.Web.Data;
using ToolShed.Web.Models;

namespace ToolShed.Web.Services;

public enum ToolFailure
{
    None,
    NotFound,
    Invalid
}

public record ToolResult(ToolFailure Failure, string? Error = null, Tool? Tool = null, ToolPhoto? Photo = null)
{
    public bool Succeeded => Failure == ToolFailure.None;

    public static ToolResult Ok(Tool tool, ToolPhoto? photo = null) => new(ToolFailure.None, null, tool, photo);

    public static ToolResult NotFound() => new(ToolFailure.NotFound, "That tool does not exist, or it is not yours.");

    public static ToolResult Invalid(string error) => new(ToolFailure.Invalid, error);
}

/// <summary>
/// Every change a member makes to their own listings, used by both the website and the apps.
/// Each method takes the caller's id and loads the tool scoped to it, so an id guessed from a
/// URL can never reach somebody else's tool.
/// </summary>
public class ToolService
{
    private readonly ApplicationDbContext _db;
    private readonly PhotoStorage _photos;
    private readonly TimeProvider _clock;

    public ToolService(ApplicationDbContext db, PhotoStorage photos, TimeProvider clock)
    {
        _db = db;
        _photos = photos;
        _clock = clock;
    }

    public async Task<ToolResult> CreateAsync(string ownerId, ToolInput input, IReadOnlyList<IFormFile> files)
    {
        var uploads = files.Where(f => f.Length > 0).ToList();
        if (uploads.Count > ImageValidator.MaxPhotosPerTool)
        {
            return ToolResult.Invalid($"Please attach no more than {ImageValidator.MaxPhotosPerTool} photos.");
        }

        var now = _clock.GetUtcNow();
        var tool = new Tool
        {
            OwnerId = ownerId,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        Apply(tool, input);

        var saved = new List<ToolPhoto>();
        foreach (var file in uploads)
        {
            var (photo, error) = await _photos.SaveAsync(file);
            if (photo is null)
            {
                // Do not leave orphaned bytes behind when one file in a batch is rejected.
                foreach (var orphan in saved)
                {
                    _photos.Delete(orphan);
                }

                return ToolResult.Invalid(error ?? "That photo could not be read.");
            }

            photo.IsPrimary = saved.Count == 0;
            saved.Add(photo);
        }

        foreach (var photo in saved)
        {
            tool.Photos.Add(photo);
        }

        _db.Tools.Add(tool);
        await _db.SaveChangesAsync();
        return ToolResult.Ok(tool);
    }

    public async Task<ToolResult> UpdateAsync(int toolId, string ownerId, ToolInput input)
    {
        var tool = await LoadOwnedAsync(toolId, ownerId);
        if (tool is null)
        {
            return ToolResult.NotFound();
        }

        Apply(tool, input);
        tool.UpdatedUtc = _clock.GetUtcNow();
        await _db.SaveChangesAsync();
        return ToolResult.Ok(tool);
    }

    public async Task<ToolResult> DeleteAsync(int toolId, string ownerId)
    {
        var tool = await LoadOwnedAsync(toolId, ownerId);
        if (tool is null)
        {
            return ToolResult.NotFound();
        }

        var files = tool.Photos.ToList();
        _db.Tools.Remove(tool);
        await _db.SaveChangesAsync();

        foreach (var photo in files)
        {
            _photos.Delete(photo);
        }

        return ToolResult.Ok(tool);
    }

    public async Task<ToolResult> AddPhotosAsync(int toolId, string ownerId, IReadOnlyList<IFormFile> files)
    {
        var tool = await LoadOwnedAsync(toolId, ownerId);
        if (tool is null)
        {
            return ToolResult.NotFound();
        }

        var incoming = files.Where(f => f.Length > 0).ToList();
        if (incoming.Count == 0)
        {
            return ToolResult.Invalid("Pick at least one photo to upload.");
        }

        var room = ImageValidator.MaxPhotosPerTool - tool.Photos.Count;
        if (incoming.Count > room)
        {
            return ToolResult.Invalid(room <= 0
                ? $"This listing already has the maximum of {ImageValidator.MaxPhotosPerTool} photos."
                : $"There is only room for {room} more photo(s) on this listing.");
        }

        ToolPhoto? last = null;
        foreach (var file in incoming)
        {
            var (photo, error) = await _photos.SaveAsync(file);
            if (photo is null)
            {
                return ToolResult.Invalid(error ?? "That photo could not be read.");
            }

            photo.IsPrimary = tool.Photos.Count == 0;
            tool.Photos.Add(photo);
            await _db.SaveChangesAsync();
            last = photo;
        }

        return ToolResult.Ok(tool, last);
    }

    public async Task<ToolResult> SetCoverAsync(int toolId, string ownerId, int photoId)
    {
        var tool = await LoadOwnedAsync(toolId, ownerId);
        if (tool is null || tool.Photos.All(p => p.Id != photoId))
        {
            return ToolResult.NotFound();
        }

        foreach (var photo in tool.Photos)
        {
            photo.IsPrimary = photo.Id == photoId;
        }

        await _db.SaveChangesAsync();
        return ToolResult.Ok(tool);
    }

    public async Task<ToolResult> DeletePhotoAsync(int toolId, string ownerId, int photoId)
    {
        var tool = await LoadOwnedAsync(toolId, ownerId);
        var target = tool?.Photos.FirstOrDefault(p => p.Id == photoId);
        if (tool is null || target is null)
        {
            return ToolResult.NotFound();
        }

        var wasCover = target.IsPrimary;
        _db.ToolPhotos.Remove(target);
        await _db.SaveChangesAsync();
        _photos.Delete(target);

        if (wasCover)
        {
            var replacement = await _db.ToolPhotos.Where(p => p.ToolId == toolId).OrderBy(p => p.Id).FirstOrDefaultAsync();
            if (replacement is not null)
            {
                replacement.IsPrimary = true;
                await _db.SaveChangesAsync();
            }
        }

        return ToolResult.Ok(tool);
    }

    private Task<Tool?> LoadOwnedAsync(int toolId, string ownerId) =>
        _db.Tools.Include(t => t.Photos).FirstOrDefaultAsync(t => t.Id == toolId && t.OwnerId == ownerId);

    private static void Apply(Tool tool, ToolInput input)
    {
        tool.Name = input.Name.Trim();
        tool.Category = input.Category.Trim();
        tool.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        tool.PickupLocation = string.IsNullOrWhiteSpace(input.PickupLocation) ? null : input.PickupLocation.Trim();
        tool.MaxLoanDays = input.MaxLoanDays;
        tool.IsListed = input.IsListed;
    }
}
