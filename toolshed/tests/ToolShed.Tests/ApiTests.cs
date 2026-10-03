using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ToolShed.Client;
using ToolShed.Contracts;
using ToolShed.Web.Models;
using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

/// <summary>
/// Drives the real portal through the same <see cref="PortalClient"/> the phone apps use. If these
/// pass, the apps and the server agree on every route, payload and rule.
/// </summary>
[Collection("portal")]
public class ApiTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[64]];

    private readonly PortalFactory _factory;

    public ApiTests(PortalFactory factory) => _factory = factory;

    // ---------- helpers ----------

    private PortalClient NewClient() => new(_factory.NewClient());

    private async Task<PortalClient> AdminAsync()
    {
        var client = NewClient();
        await client.LoginAsync(PortalFactory.AdminEmail, PortalFactory.AdminPassword, "tests");
        return client;
    }

    /// <summary>Issues an invitation straight from the service, as an admin would from the website, and returns its access code.</summary>
    private async Task<string> AccessCodeAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = (await users.FindByEmailAsync(PortalFactory.AdminEmail))!;
        var invitations = scope.ServiceProvider.GetRequiredService<InvitationService>();
        return (await invitations.IssueAsync(email, Roles.Member, 7, admin.Id)).Code;
    }

    private async Task<(PortalClient Client, string Email)> NewMemberAsync(string label)
    {
        var email = $"{label}-{Guid.NewGuid():N}@example.test";
        var client = NewClient();
        await client.RegisterAsync(email, await AccessCodeAsync(email), label, PortalFactory.MemberPassword, "Testville", "tests");
        return (client, email);
    }

    private static ToolInput Drill(string name = "Cordless drill") =>
        new(name, "Power tools", "Takes 18V batteries.", "Garage", 7);

    private static async Task<PortalApiException> ExpectFailureAsync(Func<Task> action)
    {
        var failure = await Assert.ThrowsAsync<PortalApiException>(action);
        return failure;
    }

    private async Task MutateUserAsync(string email, Func<UserManager<ApplicationUser>, ApplicationUser, Task> change)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await change(users, (await users.FindByEmailAsync(email))!);
    }

    // ---------- signing in ----------

    [Fact]
    public async Task The_api_refuses_requests_without_a_token_rather_than_redirecting()
    {
        var http = _factory.NewClient();

        foreach (var url in new[] { "/api/v1/tools", "/api/v1/me", "/api/v1/bookings", "/api/v1/tools/1" })
        {
            var response = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // A junk token is no better than none.
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_website_cookie_does_not_open_the_api()
    {
        var web = _factory.NewClient();
        await web.SignInAsync(PortalFactory.AdminEmail, PortalFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Signing_in_returns_a_working_token_and_the_member_profile()
    {
        var client = NewClient();
        var auth = await client.LoginAsync(PortalFactory.AdminEmail, PortalFactory.AdminPassword, "Adam's phone");

        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.True(auth.User.IsAdmin);

        var me = await client.GetMeAsync();
        Assert.Equal(PortalFactory.AdminEmail, me.Email, ignoreCase: true);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_address_get_the_same_answer()
    {
        var wrong = await ExpectFailureAsync(() => NewClient().LoginAsync(PortalFactory.AdminEmail, "definitely-wrong", "tests"));
        var unknown = await ExpectFailureAsync(() => NewClient().LoginAsync("nobody@example.test", "definitely-wrong", "tests"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(wrong.Message, unknown.Message);
    }

    [Fact]
    public async Task Signing_out_ends_that_device()
    {
        var client = await AdminAsync();
        var token = client.Token!;

        await client.LogoutAsync();
        Assert.Null(client.Token);

        var stale = NewClient();
        stale.Token = token;
        Assert.True((await ExpectFailureAsync(() => stale.GetMeAsync())).IsUnauthorized);
    }

    [Fact]
    public async Task An_account_is_created_from_the_app_with_email_password_and_access_code()
    {
        var email = $"app-{Guid.NewGuid():N}@example.test";
        var code = await AccessCodeAsync(email);

        var client = NewClient();
        var auth = await client.RegisterAsync(email, code, "App Member", PortalFactory.MemberPassword, null, "tests");

        Assert.Equal(email, auth.User.Email, ignoreCase: true);
        Assert.False(auth.User.IsAdmin);
        Assert.Equal(email, (await client.GetMeAsync()).Email, ignoreCase: true);

        // And the chosen password works for signing in afterwards.
        await NewClient().LoginAsync(email, PortalFactory.MemberPassword, "second phone");
    }

    [Fact]
    public async Task The_access_code_works_once_and_only_with_its_own_email()
    {
        var email = $"once-{Guid.NewGuid():N}@example.test";
        var other = $"other-{Guid.NewGuid():N}@example.test";
        var code = await AccessCodeAsync(email);
        await AccessCodeAsync(other);

        var wrongEmail = await ExpectFailureAsync(() => NewClient().RegisterAsync(other, code, "Mallory", PortalFactory.MemberPassword, null, "tests"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongEmail.StatusCode);

        await NewClient().RegisterAsync(email, code, "First", PortalFactory.MemberPassword, null, "tests");

        var again = await ExpectFailureAsync(() => NewClient().RegisterAsync(email, code, "Second", PortalFactory.MemberPassword, null, "tests"));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("do not match a valid invitation", again.Message);
    }

    [Fact]
    public async Task A_made_up_code_is_refused_and_every_failure_reads_the_same()
    {
        var email = $"fail-{Guid.NewGuid():N}@example.test";
        var code = await AccessCodeAsync(email);
        var wrong = code == "000000" ? "000001" : "000000";

        var wrongCode = await ExpectFailureAsync(() => NewClient().RegisterAsync(email, wrong, "Sneaky", PortalFactory.MemberPassword, null, "tests"));
        var unknownEmail = await ExpectFailureAsync(() => NewClient().RegisterAsync($"nobody-{Guid.NewGuid():N}@example.test", code, "Sneaky", PortalFactory.MemberPassword, null, "tests"));
        var malformed = await ExpectFailureAsync(() => NewClient().RegisterAsync(email, "12ab56", "Sneaky", PortalFactory.MemberPassword, null, "tests"));

        Assert.Equal(HttpStatusCode.BadRequest, wrongCode.StatusCode);
        Assert.Equal(wrongCode.Message, unknownEmail.Message);
        Assert.Equal(wrongCode.Message, malformed.Message);
    }

    [Fact]
    public async Task The_access_code_may_be_typed_with_a_space_or_a_dash()
    {
        var email = $"typed-{Guid.NewGuid():N}@example.test";
        var code = await AccessCodeAsync(email);

        var auth = await NewClient().RegisterAsync(email.ToUpperInvariant(), $"{code[..3]}-{code[3..]}", "Typist", PortalFactory.MemberPassword, null, "tests");
        Assert.Equal(email, auth.User.Email, ignoreCase: true);
    }

    [Fact]
    public async Task Guessing_codes_from_the_app_locks_the_invitation()
    {
        var email = $"guess-{Guid.NewGuid():N}@example.test";
        var code = await AccessCodeAsync(email);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < InvitationService.LockAfterFailures; i++)
        {
            await ExpectFailureAsync(() => NewClient().RegisterAsync(email, wrong, "Guesser", PortalFactory.MemberPassword, null, "tests"));
        }

        // Even the real code is turned away while the pause lasts.
        var locked = await ExpectFailureAsync(() => NewClient().RegisterAsync(email, code, "Owner", PortalFactory.MemberPassword, null, "tests"));
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
    }

    [Fact]
    public async Task A_weak_password_is_refused_on_registration_and_the_invitation_survives()
    {
        var email = $"weak-{Guid.NewGuid():N}@example.test";
        var code = await AccessCodeAsync(email);

        var failure = await ExpectFailureAsync(() => NewClient().RegisterAsync(email, code, "Weak", "short", null, "tests"));
        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);

        // The failed attempt did not burn the invitation.
        await NewClient().RegisterAsync(email, code, "Weak", PortalFactory.MemberPassword, null, "tests");
    }

    [Fact]
    public async Task A_token_stops_working_when_the_password_changes_or_the_member_is_suspended()
    {
        var (changed, changedEmail) = await NewMemberAsync("stamp");
        var (suspended, suspendedEmail) = await NewMemberAsync("suspend");

        await changed.GetMeAsync();
        await suspended.GetMeAsync();

        await MutateUserAsync(changedEmail, async (users, user) =>
        {
            var reset = await users.GeneratePasswordResetTokenAsync(user);
            Assert.True((await users.ResetPasswordAsync(user, reset, "Brand-New-Password-42!")).Succeeded);
        });

        await MutateUserAsync(suspendedEmail, async (users, user) =>
        {
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            await users.UpdateSecurityStampAsync(user);
        });

        Assert.True((await ExpectFailureAsync(() => changed.GetMeAsync())).IsUnauthorized);
        Assert.True((await ExpectFailureAsync(() => suspended.GetMeAsync())).IsUnauthorized);
    }

    [Fact]
    public async Task A_suspended_member_cannot_sign_in_from_the_app()
    {
        var (_, email) = await NewMemberAsync("locked");
        await MutateUserAsync(email, (users, user) => users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue));

        var failure = await ExpectFailureAsync(() => NewClient().LoginAsync(email, PortalFactory.MemberPassword, "tests"));

        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.Contains("locked", failure.Message);
    }

    // ---------- tools ----------

    [Fact]
    public async Task A_tool_can_be_listed_photographed_found_and_edited_from_the_app()
    {
        var (owner, _) = await NewMemberAsync("owner");
        var (other, _) = await NewMemberAsync("browser");

        var created = await owner.CreateToolAsync(Drill());
        Assert.True(created.IsMine);
        Assert.Empty(created.Photos);

        // Photos: the first becomes the cover; bytes come back identical, and only to members.
        var first = await owner.UploadPhotoAsync(created.Id, new MemoryStream(PngBytes), "drill.png", "image/png");
        var second = await owner.UploadPhotoAsync(created.Id, new MemoryStream(PngBytes), "drill2.png", "image/png");
        Assert.True(first.IsCover);
        Assert.False(second.IsCover);
        Assert.Equal(PngBytes, await other.GetPhotoAsync(first.Id));

        await owner.SetCoverPhotoAsync(created.Id, second.Id);
        var detail = await other.GetToolAsync(created.Id);
        Assert.False(detail.IsMine);
        Assert.Equal(second.Id, detail.Photos[0].Id);
        Assert.True(detail.Photos[0].IsCover);

        // Search and filters.
        Assert.Contains(await other.ListToolsAsync(), t => t.Id == created.Id);
        Assert.Contains(await other.ListToolsAsync(query: "cordless"), t => t.Id == created.Id);
        Assert.DoesNotContain(await other.ListToolsAsync(query: "zzzzzz"), t => t.Id == created.Id);
        Assert.Contains(await other.ListToolsAsync(category: "Power tools"), t => t.Id == created.Id);
        Assert.DoesNotContain(await other.ListToolsAsync(category: "Gardening"), t => t.Id == created.Id);
        Assert.Contains(await owner.ListToolsAsync(mine: true), t => t.Id == created.Id);
        Assert.DoesNotContain(await other.ListToolsAsync(mine: true), t => t.Id == created.Id);

        // Edit, then pause: paused tools leave everyone else's catalogue but stay in the owner's list.
        var edited = await owner.UpdateToolAsync(created.Id, Drill("Cordless drill (18V)") with { MaxLoanDays = 3 });
        Assert.Equal("Cordless drill (18V)", edited.Name);
        Assert.Equal(3, edited.MaxLoanDays);

        await owner.UpdateToolAsync(created.Id, Drill("Cordless drill (18V)") with { IsListed = false });
        Assert.DoesNotContain(await other.ListToolsAsync(), t => t.Id == created.Id);
        Assert.Contains(await owner.ListToolsAsync(mine: true), t => t.Id == created.Id && !t.IsListed);

        // Deleting a photo hands the cover to the next one; deleting the tool removes it.
        await owner.DeletePhotoAsync(created.Id, second.Id);
        Assert.Equal(first.Id, (await owner.GetToolAsync(created.Id)).Photos.Single().Id);

        await owner.DeleteToolAsync(created.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await ExpectFailureAsync(() => owner.GetToolAsync(created.Id))).StatusCode);
    }

    [Fact]
    public async Task Uploads_that_are_not_photos_and_invalid_listings_are_refused()
    {
        var (owner, _) = await NewMemberAsync("strict");
        var tool = await owner.CreateToolAsync(Drill("Strict saw"));

        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var notAPhoto = await ExpectFailureAsync(() => owner.UploadPhotoAsync(tool.Id, new MemoryStream(svg), "saw.png", "image/png"));
        Assert.Equal(HttpStatusCode.BadRequest, notAPhoto.StatusCode);
        Assert.Contains("Only JPEG, PNG, GIF and WebP", notAPhoto.Message);
        Assert.Empty((await owner.GetToolAsync(tool.Id)).Photos);

        var blank = await ExpectFailureAsync(() => owner.CreateToolAsync(Drill("") with { Category = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var tooLong = await ExpectFailureAsync(() => owner.CreateToolAsync(Drill() with { MaxLoanDays = 500 }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task The_photo_limit_is_enforced_for_the_app_too()
    {
        var (owner, _) = await NewMemberAsync("snapper");
        var tool = await owner.CreateToolAsync(Drill("Photogenic plane"));

        for (var i = 0; i < ImageValidator.MaxPhotosPerTool; i++)
        {
            await owner.UploadPhotoAsync(tool.Id, new MemoryStream(PngBytes), $"p{i}.png", "image/png");
        }

        var failure = await ExpectFailureAsync(() => owner.UploadPhotoAsync(tool.Id, new MemoryStream(PngBytes), "extra.png", "image/png"));
        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
    }

    [Fact]
    public async Task Nobody_can_change_someone_elses_tool()
    {
        var (owner, _) = await NewMemberAsync("rightful");
        var (intruder, _) = await NewMemberAsync("intruder");
        var tool = await owner.CreateToolAsync(Drill("Guarded grinder"));
        var photo = await owner.UploadPhotoAsync(tool.Id, new MemoryStream(PngBytes), "g.png", "image/png");

        Assert.Equal(HttpStatusCode.NotFound, (await ExpectFailureAsync(() => intruder.UpdateToolAsync(tool.Id, Drill("Stolen")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ExpectFailureAsync(() => intruder.DeleteToolAsync(tool.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ExpectFailureAsync(() => intruder.DeletePhotoAsync(tool.Id, photo.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ExpectFailureAsync(() => intruder.SetCoverPhotoAsync(tool.Id, photo.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ExpectFailureAsync(() =>
            intruder.UploadPhotoAsync(tool.Id, new MemoryStream(PngBytes), "x.png", "image/png"))).StatusCode);

        Assert.Equal("Guarded grinder", (await owner.GetToolAsync(tool.Id)).Name);
    }

    [Fact]
    public async Task Photos_need_a_valid_sign_in_whether_by_cookie_or_token()
    {
        var (owner, _) = await NewMemberAsync("photog");
        var tool = await owner.CreateToolAsync(Drill("Private plane"));
        var photo = await owner.UploadPhotoAsync(tool.Id, new MemoryStream(PngBytes), "p.png", "image/png");

        // No sign-in at all: sent to the website's sign-in page, as before.
        var anonymous = await _factory.NewClient().GetAsync($"/photos/{photo.Id}");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);

        // A bad token is told so plainly rather than redirected to an HTML page.
        var junk = _factory.NewClient();
        junk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await junk.GetAsync($"/photos/{photo.Id}")).StatusCode);

        // A website cookie still works.
        var web = _factory.NewClient();
        await web.SignInAsync(PortalFactory.AdminEmail, PortalFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync($"/photos/{photo.Id}")).StatusCode);
    }

    // ---------- loans ----------

    [Fact]
    public async Task A_loan_goes_from_request_to_return_entirely_through_the_app()
    {
        var (owner, _) = await NewMemberAsync("lender");
        var (borrower, _) = await NewMemberAsync("borrower");
        var tool = await owner.CreateToolAsync(Drill("Lent ladder"));

        var start = DateOnly.FromDateTime(DateTime.Now).AddDays(3);
        var booking = await borrower.RequestBookingAsync(tool.Id, start, start.AddDays(2), "For the shelves");
        Assert.Equal(BookingStatuses.Requested, booking.Status);
        Assert.False(booking.IAmOwner);

        // The owner sees it; the calendar shows the days as requested.
        var incoming = (await owner.GetBookingsAsync()).Incoming.Single(b => b.Id == booking.Id);
        Assert.True(incoming.IAmOwner);
        Assert.Equal("For the shelves", incoming.BorrowerNote);
        Assert.False((await borrower.GetToolAsync(tool.Id)).Held.Single().Approved);

        // Only the owner can decide.
        var notTheOwner = await ExpectFailureAsync(() => borrower.ApproveAsync(booking.Id));
        Assert.Equal(HttpStatusCode.BadRequest, notTheOwner.StatusCode);

        var approved = await owner.ApproveAsync(booking.Id, "Mind the rungs");
        Assert.Equal(BookingStatuses.Approved, approved.Status);
        Assert.True((await borrower.GetToolAsync(tool.Id)).Held.Single().Approved);

        var seen = (await borrower.GetBookingsAsync()).Outgoing.Single(b => b.Id == booking.Id);
        Assert.Equal("Mind the rungs", seen.OwnerNote);

        // Overlapping dates are now spoken for; the next free day is fine.
        var clash = await ExpectFailureAsync(() => borrower.RequestBookingAsync(tool.Id, start.AddDays(1), start.AddDays(3), null));
        Assert.Contains("already spoken for", clash.Message);
        await borrower.RequestBookingAsync(tool.Id, start.AddDays(3), start.AddDays(4), null);

        // Owners cannot borrow their own tools.
        var self = await ExpectFailureAsync(() => owner.RequestBookingAsync(tool.Id, start.AddDays(10), start.AddDays(11), null));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var returned = await owner.MarkReturnedAsync(booking.Id);
        Assert.Equal(BookingStatuses.Returned, returned.Status);
    }

    [Fact]
    public async Task Declining_or_cancelling_releases_the_dates()
    {
        var (owner, _) = await NewMemberAsync("decliner");
        var (borrower, _) = await NewMemberAsync("hopeful");
        var tool = await owner.CreateToolAsync(Drill("Popular post-hole digger"));
        var start = DateOnly.FromDateTime(DateTime.Now).AddDays(5);

        var first = await borrower.RequestBookingAsync(tool.Id, start, start.AddDays(1), null);
        var declined = await owner.DeclineAsync(first.Id, "Sorry, needed that weekend");
        Assert.Equal(BookingStatuses.Declined, declined.Status);
        Assert.Equal("Sorry, needed that weekend", declined.OwnerNote);
        Assert.Empty((await borrower.GetToolAsync(tool.Id)).Held);

        var second = await borrower.RequestBookingAsync(tool.Id, start, start.AddDays(1), null);
        var cancelled = await borrower.CancelAsync(second.Id);
        Assert.Equal(BookingStatuses.Cancelled, cancelled.Status);
        Assert.Empty((await owner.GetToolAsync(tool.Id)).Held);

        // A closed booking cannot be reopened.
        var again = await ExpectFailureAsync(() => owner.ApproveAsync(second.Id));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Impossible_loan_dates_are_refused_with_a_reason()
    {
        var (owner, _) = await NewMemberAsync("dated");
        var (borrower, _) = await NewMemberAsync("datee");
        var tool = await owner.CreateToolAsync(Drill("Calendar-conscious chisel") with { MaxLoanDays = 3 });
        var today = DateOnly.FromDateTime(DateTime.Now);

        var past = await ExpectFailureAsync(() => borrower.RequestBookingAsync(tool.Id, today.AddDays(-2), today, null));
        Assert.Contains("past", past.Message);

        var backwards = await ExpectFailureAsync(() => borrower.RequestBookingAsync(tool.Id, today.AddDays(5), today.AddDays(3), null));
        Assert.Contains("before", backwards.Message);

        var tooLong = await ExpectFailureAsync(() => borrower.RequestBookingAsync(tool.Id, today.AddDays(2), today.AddDays(9), null));
        Assert.Contains("at most 3", tooLong.Message);

        var missing = await ExpectFailureAsync(() => borrower.RequestBookingAsync(int.MaxValue, today.AddDays(2), today.AddDays(3), null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    // ---------- the website and the apps are one system ----------

    [Fact]
    public async Task What_happens_on_the_website_shows_up_in_the_app_and_the_other_way_round()
    {
        var (owner, _) = await NewMemberAsync("crossover");
        var web = _factory.NewClient();
        await web.SignInAsync(PortalFactory.AdminEmail, PortalFactory.AdminPassword);

        // A tool listed in the app appears in the website's catalogue.
        var tool = await owner.CreateToolAsync(Drill("Cross-platform clamp"));
        Assert.Contains("Cross-platform clamp", await web.GetHtmlAsync("/"));

        // A loan requested on the website lands in the app owner's inbox.
        var start = DateOnly.FromDateTime(DateTime.Now).AddDays(4);
        var detailsUrl = $"/Tools/Details/{tool.Id}";
        var request = await web.PostFormAsync(detailsUrl, detailsUrl,
        [
            new("Input.StartDate", start.ToString("yyyy-MM-dd")),
            new("Input.EndDate", start.AddDays(1).ToString("yyyy-MM-dd")),
            new("Input.Note", "Booked from a laptop")
        ]);
        Assert.Equal(HttpStatusCode.Redirect, request.StatusCode);

        var incoming = (await owner.GetBookingsAsync()).Incoming.Single(b => b.ToolId == tool.Id);
        Assert.Equal("Booked from a laptop", incoming.BorrowerNote);
        Assert.Equal(BookingStatuses.Requested, incoming.Status);

        // Approving it in the app shows on the website's loans page and calendar.
        await owner.ApproveAsync(incoming.Id, "Approved from a phone");
        var loans = await web.GetHtmlAsync("/Bookings");
        Assert.Contains("Approved from a phone", loans);
        Assert.Contains("day-booked", await web.GetHtmlAsync(detailsUrl));

        // And a tool listed on the website is in the app's list.
        var token = await web.AntiforgeryTokenAsync("/Tools/Create");
        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("Website-born wrench"), "Input.Name" },
            { new StringContent("Hand tools"), "Input.Category" },
            { new StringContent("7"), "Input.MaxLoanDays" }
        };
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostAsync("/Tools/Create", form)).StatusCode);
        Assert.Contains(await owner.ListToolsAsync(query: "Website-born"), t => t.Name == "Website-born wrench");
    }
}
