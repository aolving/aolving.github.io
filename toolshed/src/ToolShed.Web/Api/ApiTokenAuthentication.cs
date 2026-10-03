using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Api;

public static class ApiTokenDefaults
{
    public const string Scheme = "ApiToken";

    /// <summary>Picks the cookie or the device token per request, so a challenge only fires one handler.</summary>
    public const string SmartScheme = "CookieOrToken";

    /// <summary>Policy for API endpoints: a valid device token, nothing else.</summary>
    public const string Policy = "ApiToken";

    /// <summary>Policy for resources both the website (cookie) and the apps (token) may fetch.</summary>
    public const string MemberPolicy = "Member";
}

/// <summary>
/// Authenticates `Authorization: Bearer ...` against the hashed device tokens. Every request
/// re-checks the member, so suspending someone or changing their password takes effect at once.
/// </summary>
public class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(6);

    private readonly ApplicationDbContext _db;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly TimeProvider _clock;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApplicationDbContext db,
        SignInManager<ApplicationUser> signInManager,
        TimeProvider clock)
        : base(options, logger, encoder)
    {
        _db = db;
        _signInManager = signInManager;
        _clock = clock;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var hash = TokenGenerator.Hash(token);
        var record = await _db.ApiTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash);
        var now = _clock.GetUtcNow();

        if (record?.User is null || record.RevokedUtc is not null || record.ExpiresUtc <= now)
        {
            return AuthenticateResult.Fail("The token is not valid.");
        }

        var user = record.User;
        if (user.SecurityStamp != record.SecurityStamp || user.LockoutEnd > now)
        {
            return AuthenticateResult.Fail("The token is no longer valid.");
        }

        if (now - record.LastUsedUtc > TouchInterval)
        {
            record.LastUsedUtc = now;
            record.ExpiresUtc = now.Add(ApiTokenService.Lifetime);
            await _db.SaveChangesAsync();
        }

        var principal = await _signInManager.CreateUserPrincipalAsync(user);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
