using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;

namespace ToolShed.Web.Services;

public record InvitationIssued(Invitation Invitation, string Code);

/// <summary>
/// Issues and checks access codes. There are two kinds:
///
/// - A code tied to an email address. Joining needs that address and the code together, so a guesser
///   must know who was invited as well as the code. Wrong guesses lock that one invitation.
/// - An open code, generated ahead of time with nobody in mind. Whoever holds it joins with an address
///   of their own choosing. With no address to lock against it is weaker, so wrong guesses are counted
///   across the whole portal and trip a circuit breaker.
///
/// The two never help each other: an address that has an invitation of its own can only use that
/// invitation's code, so open codes cannot be probed through someone else's invited address.
/// </summary>
public class InvitationService
{
    /// <summary>
    /// Shown for every failure alike (no invitation, wrong code, spent, expired, locked, paused), so the
    /// form cannot be used to find out who has been invited or which codes exist.
    /// </summary>
    public const string NotValidMessage =
        "That email and access code do not match a valid invitation. Check both, or ask your administrator for a new one.";

    /// <summary>After this many wrong codes in a row, an email-bound invitation stops checking for <see cref="LockFor"/>.</summary>
    public const int LockAfterFailures = 5;

    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    /// <summary>Past this many wrong codes in total an email-bound invitation is cancelled and must be reissued.</summary>
    public const int MaxTotalFailures = 30;

    /// <summary>
    /// Wrong guesses at open codes, across the whole portal, allowed in <see cref="OpenCodeWindow"/>. Past
    /// this, open codes stop working until the window clears or an administrator resumes them. That caps
    /// guessing at this many tries a day however many machines the attacker has.
    /// </summary>
    public const int OpenCodeFailureLimit = 30;

    public static readonly TimeSpan OpenCodeWindow = TimeSpan.FromHours(24);

    /// <summary>The most codes one batch can generate.</summary>
    public const int MaxBatch = 50;

    private const int MaxCodeAttempts = 50;

    private readonly ApplicationDbContext _db;
    private readonly AccessCodeHasher _hasher;
    private readonly TimeProvider _clock;
    private readonly ILogger<InvitationService> _logger;

    public InvitationService(ApplicationDbContext db, AccessCodeHasher hasher, TimeProvider clock, ILogger<InvitationService> logger)
    {
        _db = db;
        _hasher = hasher;
        _clock = clock;
        _logger = logger;
    }

    // ------------------------------------------------------------------ issuing

    /// <summary>
    /// Issues one invitation with a fresh six-digit code that no other invitation has ever had. With an
    /// email it is tied to that address; with null it is an open code, which can only grant the Member role.
    /// The code is returned exactly once; only its keyed hash is kept.
    /// </summary>
    public async Task<InvitationIssued> IssueAsync(string? email, string role, int validForDays, string createdById, string? label = null)
    {
        var issued = await IssueCoreAsync(email, role, validForDays, createdById, label);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Invitation {Id} issued ({Kind}) by {CreatedById}", issued.Invitation.Id, issued.Invitation.IsOpen ? "open" : "email", createdById);
        return issued;
    }

    /// <summary>
    /// The code generator: one code tied to each address given, plus a number of open codes, all in one go
    /// and all unique. Either everything is issued or nothing is.
    /// </summary>
    public async Task<List<InvitationIssued>> IssueBatchAsync(
        IReadOnlyCollection<string> emails, int openCount, string role, int validForDays, string createdById, string? label = null)
    {
        if (openCount < 0 || emails.Count + openCount is < 1 or > MaxBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(openCount), $"A batch holds between 1 and {MaxBatch} codes.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();

        var issued = new List<InvitationIssued>(emails.Count + openCount);
        foreach (var email in emails)
        {
            issued.Add(await IssueCoreAsync(email, role, validForDays, createdById, label));
        }

        for (var i = 0; i < openCount; i++)
        {
            issued.Add(await IssueCoreAsync(null, Roles.Member, validForDays, createdById, label));
        }

        await PruneOldFailuresAsync();
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation("Batch of {Count} access codes issued by {CreatedById}", issued.Count, createdById);
        return issued;
    }

    private async Task<InvitationIssued> IssueCoreAsync(string? email, string role, int validForDays, string createdById, string? label)
    {
        if (!Roles.All.Contains(role))
        {
            throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        }

        if (string.IsNullOrWhiteSpace(email) && role != Roles.Member)
        {
            // Anyone holding an open code can use it, so it must never be able to make an administrator.
            throw new ArgumentException("Open codes can only grant the Member role.", nameof(role));
        }

        var now = _clock.GetUtcNow();
        var normalised = string.IsNullOrWhiteSpace(email) ? null : Normalise(email);

        if (normalised is not null)
        {
            // Supersede any invitation still outstanding for this address, so re-inviting somebody does
            // not leave two live codes for one mailbox.
            var outstanding = await _db.Invitations
                .Where(i => i.Email == normalised && i.RedeemedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now)
                .ToListAsync();

            foreach (var stale in outstanding)
            {
                stale.RevokedUtc = now;
            }
        }

        var (code, hash) = await NewUniqueCodeAsync();
        var invitation = new Invitation
        {
            Email = normalised,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            CodeHash = hash,
            Role = role,
            CreatedById = createdById,
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(Math.Clamp(validForDays, 1, 30))
        };

        _db.Invitations.Add(invitation);

        // Saved straight away so the next code's uniqueness check in this batch can see this one.
        await _db.SaveChangesAsync();
        return new InvitationIssued(invitation, code);
    }

    // ------------------------------------------------------------------ checking

    /// <summary>
    /// Checks an email and access code, returning the invitation they unlock or null with no hint as to why.
    /// If the address has an invitation of its own, only that invitation's code can work. Otherwise only an
    /// open code can.
    /// </summary>
    public async Task<Invitation?> VerifyAsync(string? email, string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(email) || !AccessCodes.TryNormalise(rawCode, out var code))
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        var normalised = Normalise(email);

        var tied = await _db.Invitations
            .Where(i => i.Email == normalised && i.RedeemedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now)
            .OrderByDescending(i => i.CreatedUtc)
            .FirstOrDefaultAsync();

        return tied is not null
            ? await VerifyTiedAsync(tied, code, now)
            : await VerifyOpenAsync(code, now);
    }

    /// <summary>
    /// A wrong code counts against the invitation: five in a row pause checking for fifteen minutes, and
    /// thirty cancel it, which caps the odds of guessing a code at 30 in a million.
    /// </summary>
    private async Task<Invitation?> VerifyTiedAsync(Invitation invitation, string code, DateTimeOffset now)
    {
        if (invitation.IsLockedAt(now))
        {
            return null;
        }

        if (_hasher.Matches(invitation.CodeHash, code))
        {
            return invitation;
        }

        invitation.FailedAttempts++;
        if (invitation.FailedAttempts >= MaxTotalFailures)
        {
            invitation.RevokedUtc = now;
            _logger.LogWarning("Invitation {Id} cancelled after {Count} wrong access codes", invitation.Id, invitation.FailedAttempts);
        }
        else if (invitation.FailedAttempts % LockAfterFailures == 0)
        {
            invitation.LockedUntilUtc = now.Add(LockFor);
            _logger.LogWarning("Invitation {Id} paused after {Count} wrong access codes", invitation.Id, invitation.FailedAttempts);
        }

        await _db.SaveChangesAsync();
        return null;
    }

    /// <summary>
    /// Once <see cref="OpenCodeFailureLimit"/> wrong guesses have been made across the portal in a day,
    /// open codes are not even looked at: neither a right nor a wrong one gets through until the oldest
    /// guesses age out, or an administrator resumes them.
    /// </summary>
    private async Task<Invitation?> VerifyOpenAsync(string code, DateTimeOffset now)
    {
        if (await RecentOpenFailuresAsync(now) >= OpenCodeFailureLimit)
        {
            return null;
        }

        var hash = _hasher.Hash(code);
        var open = await _db.Invitations.FirstOrDefaultAsync(i =>
            i.Email == null && i.CodeHash == hash && i.RedeemedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now);

        if (open is not null)
        {
            return open;
        }

        _db.OpenCodeFailures.Add(new OpenCodeFailure { AtUtc = now });
        await _db.SaveChangesAsync();

        if (await RecentOpenFailuresAsync(now) == OpenCodeFailureLimit)
        {
            _logger.LogWarning("Open access codes paused: {Limit} wrong guesses in {Hours} hours", OpenCodeFailureLimit, OpenCodeWindow.TotalHours);
        }

        return null;
    }

    // ------------------------------------------------------------------ the circuit breaker

    private Task<int> RecentOpenFailuresAsync(DateTimeOffset now)
    {
        var since = now - OpenCodeWindow;
        return _db.OpenCodeFailures.CountAsync(f => f.AtUtc > since);
    }

    public async Task<OpenCodeStatus> OpenCodeStatusAsync()
    {
        var failures = await RecentOpenFailuresAsync(_clock.GetUtcNow());
        return new OpenCodeStatus(failures, failures >= OpenCodeFailureLimit);
    }

    /// <summary>An administrator, having looked into it, turns open codes back on.</summary>
    public async Task ResumeOpenCodesAsync()
    {
        await _db.OpenCodeFailures.ExecuteDeleteAsync();
        _logger.LogInformation("Open access codes resumed by an administrator");
    }

    private Task PruneOldFailuresAsync()
    {
        var cutoff = _clock.GetUtcNow() - TimeSpan.FromDays(7);
        return _db.OpenCodeFailures.Where(f => f.AtUtc < cutoff).ExecuteDeleteAsync();
    }

    // ------------------------------------------------------------------ the rest

    /// <summary>Marks the invitation spent. Call after the account has been created.</summary>
    public async Task RedeemAsync(Invitation invitation, string userId)
    {
        invitation.RedeemedUtc = _clock.GetUtcNow();
        invitation.RedeemedByUserId = userId;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Invitation {Id} redeemed by {UserId}", invitation.Id, userId);
    }

    public async Task<bool> RevokeAsync(int invitationId)
    {
        var invitation = await _db.Invitations.FindAsync(invitationId);
        if (invitation is null || invitation.IsRedeemed || invitation.IsRevoked)
        {
            return false;
        }

        invitation.RevokedUtc = _clock.GetUtcNow();
        await _db.SaveChangesAsync();
        return true;
    }

    public static string Normalise(string email) => email.Trim().ToUpperInvariant();

    /// <summary>The address the account is created with: the invited one, or for an open code the one typed.</summary>
    public static string AccountEmail(Invitation invitation, string typedEmail) =>
        (invitation.Email ?? Normalise(typedEmail)).ToLowerInvariant();

    /// <summary>A code no invitation, spent or not, has used, so each code identifies exactly one invitation.</summary>
    private async Task<(string Code, string Hash)> NewUniqueCodeAsync()
    {
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = AccessCodes.Generate();
            var hash = _hasher.Hash(code);
            if (!await _db.Invitations.AnyAsync(i => i.CodeHash == hash))
            {
                return (code, hash);
            }
        }

        throw new InvalidOperationException("Could not find an unused access code. Too many invitations have been issued.");
    }
}

public record OpenCodeStatus(int RecentFailures, bool Paused);
