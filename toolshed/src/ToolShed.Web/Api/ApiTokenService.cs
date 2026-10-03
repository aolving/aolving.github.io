using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Api;

public class ApiTokenService
{
    /// <summary>How long an idle device stays signed in. Each day of use pushes it forward again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Beyond this many live devices, the least recently used is signed out.</summary>
    public const int MaxDevicesPerMember = 10;

    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _clock;

    public ApiTokenService(ApplicationDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The plain token is returned exactly once; only its hash is kept.</summary>
    public async Task<(string Token, ApiToken Record)> IssueAsync(ApplicationUser user, string? deviceName)
    {
        var now = _clock.GetUtcNow();

        var live = await _db.ApiTokens
            .Where(t => t.UserId == user.Id && t.RevokedUtc == null && t.ExpiresUtc > now)
            .OrderBy(t => t.LastUsedUtc)
            .ToListAsync();

        foreach (var stale in live.Take(Math.Max(0, live.Count - (MaxDevicesPerMember - 1))))
        {
            stale.RevokedUtc = now;
        }

        var token = TokenGenerator.NewToken();
        var record = new ApiToken
        {
            UserId = user.Id,
            TokenHash = TokenGenerator.Hash(token),
            DeviceName = Clean(deviceName),
            SecurityStamp = user.SecurityStamp ?? string.Empty,
            CreatedUtc = now,
            LastUsedUtc = now,
            ExpiresUtc = now.Add(Lifetime)
        };

        _db.ApiTokens.Add(record);
        await _db.SaveChangesAsync();
        return (token, record);
    }

    public async Task RevokeAsync(string token)
    {
        var hash = TokenGenerator.Hash(token);
        var record = await _db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (record is not null && record.RevokedUtc is null)
        {
            record.RevokedUtc = _clock.GetUtcNow();
            await _db.SaveChangesAsync();
        }
    }

    private static string Clean(string? deviceName)
    {
        var trimmed = deviceName?.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "Unnamed device";
        }

        return trimmed.Length > 80 ? trimmed[..80] : trimmed;
    }
}
