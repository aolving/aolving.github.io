using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ToolShed.Contracts;

namespace ToolShed.Client;

/// <summary>A failure reported by the portal. The message is written for the person using the app.</summary>
public class PortalApiException : Exception
{
    public PortalApiException(HttpStatusCode statusCode, string message) : base(message) => StatusCode = statusCode;

    public HttpStatusCode StatusCode { get; }

    /// <summary>True when the stored sign-in no longer works and the member needs to sign in again.</summary>
    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;
}

/// <summary>
/// A typed wrapper over the portal's JSON API. Construct it with an HttpClient whose BaseAddress is
/// the portal's address (with a trailing slash), and set <see cref="Token"/> after signing in.
/// </summary>
public sealed class PortalClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public PortalClient(HttpClient http) => _http = http;

    /// <summary>The device token from sign-in. Sent as a bearer token on every call once set.</summary>
    public string? Token { get; set; }

    // ---------------------------------------------------------------- auth

    public async Task<AuthResponse> LoginAsync(string email, string password, string? deviceName, CancellationToken ct = default)
    {
        var response = await SendAsync<AuthResponse>(HttpMethod.Post, "api/v1/auth/login",
            new LoginRequest(email, password, deviceName), ct);
        Token = response.Token;
        return response;
    }

    /// <summary>Creates an account. The access code can be typed as "123 456", "123-456" or "123456".</summary>
    public async Task<AuthResponse> RegisterAsync(
        string email, string accessCode, string displayName, string password, string? location, string? deviceName, CancellationToken ct = default)
    {
        var response = await SendAsync<AuthResponse>(HttpMethod.Post, "api/v1/auth/register",
            new RegisterRequest(email, accessCode, displayName, password, location, deviceName), ct);
        Token = response.Token;
        return response;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await SendAsync(HttpMethod.Post, "api/v1/auth/logout", null, ct);
        }
        finally
        {
            Token = null;
        }
    }

    public Task<UserDto> GetMeAsync(CancellationToken ct = default) =>
        SendAsync<UserDto>(HttpMethod.Get, "api/v1/me", null, ct);

    // ---------------------------------------------------------------- tools

    public Task<List<ToolSummaryDto>> ListToolsAsync(string? query = null, string? category = null, bool mine = false, CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add("query=" + Uri.EscapeDataString(query.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            parts.Add("category=" + Uri.EscapeDataString(category));
        }

        if (mine)
        {
            parts.Add("mine=true");
        }

        var path = "api/v1/tools" + (parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty);
        return SendAsync<List<ToolSummaryDto>>(HttpMethod.Get, path, null, ct);
    }

    public Task<ToolDetailDto> GetToolAsync(int id, CancellationToken ct = default) =>
        SendAsync<ToolDetailDto>(HttpMethod.Get, $"api/v1/tools/{id}", null, ct);

    public Task<ToolDetailDto> CreateToolAsync(ToolInput input, CancellationToken ct = default) =>
        SendAsync<ToolDetailDto>(HttpMethod.Post, "api/v1/tools", input, ct);

    public Task<ToolDetailDto> UpdateToolAsync(int id, ToolInput input, CancellationToken ct = default) =>
        SendAsync<ToolDetailDto>(HttpMethod.Put, $"api/v1/tools/{id}", input, ct);

    public Task DeleteToolAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/v1/tools/{id}", null, ct);

    public async Task<PhotoDto> UploadPhotoAsync(int toolId, Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        using var response = await SendRawAsync(HttpMethod.Post, $"api/v1/tools/{toolId}/photos", form, ct);
        return await ReadAsync<PhotoDto>(response, ct);
    }

    public Task SetCoverPhotoAsync(int toolId, int photoId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/tools/{toolId}/photos/{photoId}/cover", null, ct);

    public Task DeletePhotoAsync(int toolId, int photoId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/v1/tools/{toolId}/photos/{photoId}", null, ct);

    /// <summary>
    /// Photos are private to signed-in members, so an app cannot hand a bare URL to an image view.
    /// It fetches the bytes here (with the token) and builds the image from them.
    /// </summary>
    public async Task<byte[]> GetPhotoAsync(int photoId, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"photos/{photoId}", null, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ---------------------------------------------------------------- bookings

    public Task<BookingDto> RequestBookingAsync(int toolId, DateOnly start, DateOnly end, string? note, CancellationToken ct = default) =>
        SendAsync<BookingDto>(HttpMethod.Post, $"api/v1/tools/{toolId}/bookings", new BookingRequestInput(start, end, note), ct);

    public Task<BookingsDto> GetBookingsAsync(CancellationToken ct = default) =>
        SendAsync<BookingsDto>(HttpMethod.Get, "api/v1/bookings", null, ct);

    public Task<BookingDto> ApproveAsync(int bookingId, string? note = null, CancellationToken ct = default) =>
        SendAsync<BookingDto>(HttpMethod.Post, $"api/v1/bookings/{bookingId}/approve", new DecisionInput(note), ct);

    public Task<BookingDto> DeclineAsync(int bookingId, string? note = null, CancellationToken ct = default) =>
        SendAsync<BookingDto>(HttpMethod.Post, $"api/v1/bookings/{bookingId}/decline", new DecisionInput(note), ct);

    public Task<BookingDto> CancelAsync(int bookingId, CancellationToken ct = default) =>
        SendAsync<BookingDto>(HttpMethod.Post, $"api/v1/bookings/{bookingId}/cancel", null, ct);

    public Task<BookingDto> MarkReturnedAsync(int bookingId, CancellationToken ct = default) =>
        SendAsync<BookingDto>(HttpMethod.Post, $"api/v1/bookings/{bookingId}/returned", null, ct);

    // ---------------------------------------------------------------- plumbing

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, ToContent(body), ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, ToContent(body), ct);
    }

    private static HttpContent? ToContent(object? body) =>
        body is null ? null : JsonContent.Create(body, body.GetType(), options: Json);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
        return value ?? throw new PortalApiException(response.StatusCode, "The portal sent an empty response.");
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrEmpty(Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }

        var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var message = await ReadErrorAsync(response, ct);
        var status = response.StatusCode;
        response.Dispose();
        throw new PortalApiException(status, message);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(Json, ct);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Not one of ours (a proxy error page, say); fall through to the generic wording.
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Please sign in again.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a few minutes and try again.",
            HttpStatusCode.RequestEntityTooLarge => "That file is too large.",
            _ => $"The portal returned an unexpected response ({(int)response.StatusCode})."
        };
    }
}
