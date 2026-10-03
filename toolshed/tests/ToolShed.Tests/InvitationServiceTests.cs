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

    // ---------------------------------------------------------------- open codes

    private Task<InvitationIssued> IssueOpen(string? label = null) => _service.IssueAsync(null, Roles.Member, 7, _adminId, label);

    [Fact]
    public async Task An_open_code_has_no_email_and_works_with_whatever_address_the_holder_chooses()
    {
        var open = await IssueOpen("Street party");

        Assert.True(open.Invitation.IsOpen);
        Assert.Null(open.Invitation.Email);
        Assert.Equal("Street party", open.Invitation.Label);
        Assert.Equal(open.Invitation.Id, (await _service.VerifyAsync("anyone@example.test", open.Code))?.Id);
        Assert.Equal(open.Invitation.Id, (await _service.VerifyAsync("someone.else@example.test", open.Code))?.Id);
    }

    [Fact]
    public async Task An_open_code_works_once()
    {
        var open = await IssueOpen();
        var invitation = await _service.VerifyAsync("first@example.test", open.Code);
        await _service.RedeemAsync(invitation!, "user-1");

        Assert.Null(await _service.VerifyAsync("second@example.test", open.Code));
    }

    [Fact]
    public async Task An_open_code_expires_and_can_be_revoked()
    {
        var expiring = await IssueOpen();
        var revoked = await IssueOpen();
        await _service.RevokeAsync(revoked.Invitation.Id);

        Assert.Null(await _service.VerifyAsync("a@example.test", revoked.Code));
        Assert.NotNull(await _service.VerifyAsync("a@example.test", expiring.Code));

        _clock.Now = _clock.Now.AddDays(7).AddSeconds(1);
        Assert.Null(await _service.VerifyAsync("a@example.test", expiring.Code));
    }

    [Fact]
    public async Task An_open_code_can_never_make_an_administrator()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.IssueAsync(null, Roles.Admin, 7, _adminId));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.IssueAsync("  ", Roles.Admin, 7, _adminId));

        var batch = await _service.IssueBatchAsync(["boss@example.test"], 2, Roles.Admin, 7, _adminId);
        Assert.Equal(Roles.Admin, batch[0].Invitation.Role);
        Assert.All(batch.Skip(1), i => Assert.Equal(Roles.Member, i.Invitation.Role));
    }

    [Fact]
    public async Task An_address_with_its_own_invitation_cannot_use_an_open_code()
    {
        var tied = await Issue("invited@example.test");
        var open = await IssueOpen();

        // This address has its own invitation, so only that invitation's code can work for it.
        Assert.Null(await _service.VerifyAsync("invited@example.test", open.Code));
        Assert.NotNull(await _service.VerifyAsync("invited@example.test", tied.Code));
    }

    [Fact]
    public async Task A_tied_code_does_not_work_as_an_open_code_for_another_address()
    {
        var tied = await Issue("invited@example.test");

        Assert.Null(await _service.VerifyAsync("stranger@example.test", tied.Code));
    }

    [Fact]
    public async Task Wrong_guesses_against_an_invited_address_never_count_against_open_codes()
    {
        var tied = await Issue("invited@example.test");
        var wrong = WrongCodeFor(tied.Code);
        for (var i = 0; i < 4; i++)
        {
            await _service.VerifyAsync("invited@example.test", wrong);
        }

        Assert.Equal(0, (await _service.OpenCodeStatusAsync()).RecentFailures);
    }

    [Fact]
    public async Task Malformed_input_does_not_count_as_an_open_code_guess()
    {
        await IssueOpen();

        foreach (var junk in new[] { "", "12345", "abcdef", "12 34 5x" })
        {
            await _service.VerifyAsync("x@example.test", junk);
        }

        Assert.Equal(0, (await _service.OpenCodeStatusAsync()).RecentFailures);
    }

    [Fact]
    public async Task Thirty_wrong_open_guesses_pause_open_codes_even_for_the_right_one()
    {
        var open = await IssueOpen();
        var wrong = WrongCodeFor(open.Code);

        for (var i = 0; i < InvitationService.OpenCodeFailureLimit; i++)
        {
            Assert.Null(await _service.VerifyAsync($"guesser{i}@example.test", wrong));
        }

        var status = await _service.OpenCodeStatusAsync();
        Assert.True(status.Paused);
        Assert.Equal(InvitationService.OpenCodeFailureLimit, status.RecentFailures);

        // However many machines the guesses come from, the real code is turned away too.
        Assert.Null(await _service.VerifyAsync("honest@example.test", open.Code));
    }

    [Fact]
    public async Task While_paused_further_guesses_are_not_even_recorded()
    {
        var open = await IssueOpen();
        var wrong = WrongCodeFor(open.Code);
        for (var i = 0; i < InvitationService.OpenCodeFailureLimit + 20; i++)
        {
            await _service.VerifyAsync($"guesser{i}@example.test", wrong);
        }

        Assert.Equal(InvitationService.OpenCodeFailureLimit, await _db.OpenCodeFailures.CountAsync());
    }

    [Fact]
    public async Task Open_codes_pause_does_not_touch_codes_tied_to_an_email()
    {
        var open = await IssueOpen();
        var tied = await Issue("invited@example.test");
        for (var i = 0; i < InvitationService.OpenCodeFailureLimit; i++)
        {
            await _service.VerifyAsync($"guesser{i}@example.test", WrongCodeFor(open.Code));
        }

        Assert.True((await _service.OpenCodeStatusAsync()).Paused);
        Assert.NotNull(await _service.VerifyAsync("invited@example.test", tied.Code));
    }

    [Fact]
    public async Task Open_codes_come_back_when_the_old_guesses_age_out()
    {
        var open = await IssueOpen();
        for (var i = 0; i < InvitationService.OpenCodeFailureLimit; i++)
        {
            await _service.VerifyAsync($"guesser{i}@example.test", WrongCodeFor(open.Code));
        }

        _clock.Now = _clock.Now.Add(InvitationService.OpenCodeWindow).AddSeconds(1);

        Assert.False((await _service.OpenCodeStatusAsync()).Paused);
        Assert.NotNull(await _service.VerifyAsync("honest@example.test", open.Code));
    }

    [Fact]
    public async Task An_administrator_can_resume_open_codes_early()
    {
        var open = await IssueOpen();
        for (var i = 0; i < InvitationService.OpenCodeFailureLimit; i++)
        {
            await _service.VerifyAsync($"guesser{i}@example.test", WrongCodeFor(open.Code));
        }

        await _service.ResumeOpenCodesAsync();

        Assert.False((await _service.OpenCodeStatusAsync()).Paused);
        Assert.NotNull(await _service.VerifyAsync("honest@example.test", open.Code));
    }

    [Fact]
    public async Task The_account_takes_the_invited_address_or_for_an_open_code_the_one_typed()
    {
        var tied = await Issue("Invited@Example.Test");
        var open = await IssueOpen();

        Assert.Equal("invited@example.test", InvitationService.AccountEmail(tied.Invitation, "whatever@example.test"));
        Assert.Equal("chosen@example.test", InvitationService.AccountEmail(open.Invitation, "  Chosen@Example.Test "));
    }

    // ---------------------------------------------------------------- the generator (batches)

    [Fact]
    public async Task A_batch_issues_one_code_per_address_plus_the_open_codes_all_unique()
    {
        var batch = await _service.IssueBatchAsync(["a@example.test", "b@example.test"], 5, Roles.Member, 7, _adminId, "Handout");

        Assert.Equal(7, batch.Count);
        Assert.Equal(7, batch.Select(b => b.Code).Distinct().Count());
        Assert.Equal(2, batch.Count(b => !b.Invitation.IsOpen));
        Assert.Equal(5, batch.Count(b => b.Invitation.IsOpen));
        Assert.All(batch, b => Assert.Equal("Handout", b.Invitation.Label));
        Assert.All(batch, b => Assert.Matches("^[0-9]{6}$", b.Code));
        Assert.Equal(7, await _db.Invitations.CountAsync());
    }

    [Fact]
    public async Task Every_code_in_a_batch_unlocks_exactly_its_own_invitation()
    {
        var batch = await _service.IssueBatchAsync(["a@example.test"], 3, Roles.Member, 7, _adminId);

        Assert.Equal(batch[0].Invitation.Id, (await _service.VerifyAsync("a@example.test", batch[0].Code))?.Id);
        foreach (var open in batch.Skip(1))
        {
            Assert.Equal(open.Invitation.Id, (await _service.VerifyAsync("new@example.test", open.Code))?.Id);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 51)]
    [InlineData(2, -1)]
    public async Task Empty_oversized_or_negative_batches_are_refused_and_issue_nothing(int emails, int open)
    {
        var addresses = Enumerable.Range(0, emails).Select(i => $"p{i}@example.test").ToList();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.IssueBatchAsync(addresses, open, Roles.Member, 7, _adminId));
        Assert.Equal(0, await _db.Invitations.CountAsync());
    }

    [Fact]
    public async Task A_batch_with_an_unknown_role_issues_nothing()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.IssueBatchAsync(["a@example.test", "b@example.test"], 2, "Superuser", 7, _adminId));

        Assert.Equal(0, await _db.Invitations.CountAsync());
    }

    [Fact]
    public async Task A_batch_retires_earlier_codes_for_the_same_address()
    {
        var first = await Issue("again@example.test");
        var batch = await _service.IssueBatchAsync(["again@example.test"], 0, Roles.Member, 7, _adminId);

        Assert.Null(await _service.VerifyAsync("again@example.test", first.Code));
        Assert.NotNull(await _service.VerifyAsync("again@example.test", batch[0].Code));
    }

    [Fact]
    public async Task Unknown_roles_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.IssueAsync("x@example.test", "Superuser", 7, _adminId));
    }
}
