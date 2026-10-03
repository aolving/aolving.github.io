using System.Collections.Concurrent;

namespace ToolShed.Mobile.Services;

/// <summary>
/// Photos are private to signed-in members, so an image view cannot simply be given a URL. They are
/// fetched with the sign-in token and kept in memory for the session.
/// </summary>
public sealed class PhotoCache
{
    private readonly PortalSession _session;
    private readonly ConcurrentDictionary<int, byte[]> _bytes = new();

    // A long list of tools should not open dozens of connections at once.
    private readonly SemaphoreSlim _gate = new(4);

    public PhotoCache(PortalSession session) => _session = session;

    public async Task<ImageSource?> GetAsync(int photoId)
    {
        if (!_bytes.TryGetValue(photoId, out var bytes))
        {
            await _gate.WaitAsync();
            try
            {
                bytes = await _session.Client.GetPhotoAsync(photoId);
                _bytes[photoId] = bytes;
            }
            catch (Exception ex) when (ex is ToolShed.Client.PortalApiException or HttpRequestException or TaskCanceledException)
            {
                // A missing picture should leave a blank tile, not an error dialog per photo.
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        return ImageSource.FromStream(() => new MemoryStream(bytes));
    }

    public void Forget(int photoId) => _bytes.TryRemove(photoId, out _);

    public void Clear() => _bytes.Clear();
}
