namespace ToolShed.Web.Services;

/// <summary>
/// The portal ships no inline script or style, so the policy can stay strict.
/// </summary>
public class SecurityHeadersMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "img-src 'self' data:; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), interest-cohort=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        return _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
