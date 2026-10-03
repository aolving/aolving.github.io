using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

/// <summary>The access-code rules, against a real SQLite database and a clock the test controls.</summary>
public sealed class InvitationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly FixedClock _clock = new();
    private readonly AccessCodeHasher _hasher = new(Enumerable.Repeat((byte)7, 32).ToArray());
    private readonly InvitationService _service;
    private readonly string _adminId;

    public InvitationServiceTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var admin = new ApplicationUser { UserName = "admin", Email = "admin@example.test", DisplayName = "Admin" };
        _db.Users.Add(admin);
        _db.SaveChanges();
        _adminId = admin.Id;

        _service = new InvitationService(_db, _hasher, _clock, NullLogger<InvitationService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<InvitationIssued> Issue(string email = "new@example.test") => _service.IssueAsync(email, Roles.Member, 7, _adminId);

    private static string WrongCodeFor(string code) => code == "000000" ? "000001" : "000000";

    [Fact]
    public async Task An_issued_code_is_six_digits_and_unlocks_exactly_its_own_invitation()
    {
        var issued = await Issue();

        Assert.Equal(6, issued.Code.Length);
        var found = await _service.VerifyAsync("new@example.test", issued.Code);
        Assert.Equal(issued.Invitation.Id, found?.Id);
    }

    [Fact]
    public async Task The_code_is_not_stored_in_the_clear()
    {
        var issued = await Issue();

        var stored = await _db.Invitations.AsNoTracking().SingleAsync();
        Assert.NotEqual(issued.Code, stored.CodeHash);
        Assert.DoesNotContain(issued.Code, stored.CodeHash);
        Assert.Equal(64, stored.CodeHash.Length);
    }

    [Fact]
    public async Task Every_code_is_unique_across_all_invitations_ever_issued()
    {
        var codes = new HashSet<string>();
        for (var i = 0; i < 300; i++)
        {
            var issued = await Issue($"person{i}@example.test");
            Assert.True(codes.Add(issued.Code), $"code {issued.Code} was issued twice");
        }

        Assert.Equal(300, await _db.Invitations.Select(i => i.CodeHash).Distinct().CountAsync());
    }

    [Fact]
    public async Task A_code_works_only_with_the_email_it_was_issued_to()
    {
        var issued = await Issue("alice@example.test");
        await Issue("bob@example.test");

        Assert.Null(await _service.VerifyAsync("bob@example.test", issued.Code));
        Assert.Null(await _service.VerifyAsync("carol@example.test", issued.Code));
        Assert.NotNull(await _service.VerifyAsync("alice@example.test", issued.Code));
    }

    [Theory]
    [InlineData("ALICE@EXAMPLE.TEST")]
    [InlineData("  alice@example.test  ")]
    public async Task The_email_is_matched_without_regard_to_case_or_stray_spaces(string typed)
    {
        var issued = await Issue("alice@example.test");
        Assert.NotNull(await _service.VerifyAsync(typed, issued.Code));
    }

    [Fact]
    public async Task The_code_is_accepted_however_it_was_typed()
    {
        var issued = await Issue();
        var spaced = $"{issued.Code[..3]} {issued.Code[3..]}";
        var dashed = $"{issued.Code[..3]}-{issued.Code[3..]}";

        Assert.NotNull(await _service.VerifyAsync("new@example.test", spaced));
        Assert.NotNull(await _service.VerifyAsync("new@example.test", dashed));
    }

    [Theory]
    [InlineData(null, "123456")]
    [InlineData("", "123456")]
    [InlineData("new@example.test", null)]
    [InlineData("new@example.test", "")]
    [InlineData("new@example.test", "12345")]
    [InlineData("new@example.test", "abcdef")]
    public async Task Missing_or_malformed_input_is_refused_without_counting_as_a_guess(string? email, string? code)
    {
        await Issue();

        Assert.Null(await _service.VerifyAsync(email, code));
        Assert.Equal(0, (await _db.Invitations.AsNoTracking().SingleAsync()).FailedAttempts);
    }

    [Fact]
    public async Task A_spent_invitation_cannot_be_used_again()
    {
        var issued = await Issue();
        await _service.RedeemAsync(issued.Invitation, "someone");

        Assert.Null(await _service.VerifyAsync("new@example.test", issued.Code));
    }

    [Fact]
    public async Task A_revoked_invitation_cannot_be_used()
    {
        var issued = await Issue();
        Assert.True(await _service.RevokeAsync(issued.Invitation.Id));

        Assert.Null(await _service.VerifyAsync("new@example.test", issued.Code));
    }

    [Fact]
    public async Task An_invitation_expires()
    {
        var issued = await Issue();
        Assert.NotNull(await _service.VerifyAsync("new@example.test", issued.Code));

        _clock.Now = _clock.Now.AddDays(7).AddSeconds(1);
        Assert.Null(await _service.VerifyAsync("new@example.test", issued.Code));
    }

    [Fact]
    public async Task Reissuing_for_an_address_retires_the_earlier_code()
    {
        var first = await Issue("alice@example.test");
        var second = await Issue("alice@example.test");

        Assert.Null(await _service.VerifyAsync("alice@example.test", first.Code));
        Assert.NotNull(await _service.VerifyAsync("alice@example.test", second.Code));
    }

    [Fact]
    public async Task Five_wrong_codes_pause_checking_even_for_the_right_code_and_then_it_resumes()
    {
        var issued = await Issue();
        var wrong = WrongCodeFor(issued.Code);

        for (var i = 0; i < InvitationService.LockAfterFailures; i++)
        {
            Assert.Null(await _service.VerifyAsync("new@example.test", wrong));
        }

        // Locked: the correct code is refused too, so guessing cannot simply carry on.
        Assert.Null(await _service.VerifyAsync("new@example.test", issued.Code));

        _clock.Now = _clock.Now.Add(InvitationService.LockFor).AddSeconds(1);
        Assert.NotNull(await _service.VerifyAsync("new@example.test", issued.Code));
    }

    [Fact]
    public async Task Refused_attempts_while_locked_do_not_extend_the_lock_or_the_count()
    {
        var issued = await Issue();
        var wrong = WrongCodeFor(issued.Code);
        for (var i = 0; i < InvitationService.LockAfterFailures; i++)
        {
            await _service.VerifyAsync("new@example.test", wrong);
        }

        for (var i = 0; i < 20; i++)
        {
            await _service.VerifyAsync("new@example.test", wrong);
        }

        Assert.Equal(InvitationService.LockAfterFailures, (await _db.Invitations.AsNoTracking().SingleAsync()).FailedAttempts);
    }

    [Fact]
    public async Task Repeated_guessing_eventually_cancels_the_invitation_for_good()
    {
        var issued = await Issue();
        var wrong = WrongCodeFor(issued.Code);

        var attempts = 0;
        while (attempts < InvitationService.MaxTotalFailures)
        {
            // Wait out each pause, as a patient attacker would.
            _clock.Now = _clock.Now.Add(InvitationService.LockFor).AddSeconds(1);
            await _service.VerifyAsync("new@example.test", wrong);
            attempts++;
        }

        var stored = await _db.Invitations.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.RevokedUtc);

        _clock.Now = _clock.Now.AddDays(1);
        Assert.Null(await _service.VerifyAsync("new@example.test", issued.Code));
    }

    [Fact]
    public async Task Guessing_against_one_address_does_not_lock_another()
    {
        var alice = await Issue("alice@example.test");
        var bob = await Issue("bob@example.test");
        for (var i = 0; i < InvitationService.LockAfterFailures; i++)
        {
            await _service.VerifyAsync("alice@example.test", WrongCodeFor(alice.Code));
        }

        Assert.NotNull(await _service.VerifyAsync("bob@example.test", bob.Code));
    }

    [Fact]
    public async Task A_correct_code_for_an_address_with_no_invitation_has_nothing_to_lock()
    {
        var issued = await Issue("alice@example.test");

        Assert.Null(await _service.VerifyAsync("nobody@example.test", issued.Code));
        Assert.Equal(0, (await _db.Invitations.AsNoTracking().SingleAsync()).FailedAttempts);
    }

    [Fact]
    public async Task Unknown_roles_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.IssueAsync("x@example.test", "Superuser", 7, _adminId));
    }
}
