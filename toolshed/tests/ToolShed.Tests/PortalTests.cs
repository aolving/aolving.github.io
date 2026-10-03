using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ToolShed.Web.Models;
using Xunit;

namespace ToolShed.Tests;

/// <summary>
/// End-to-end checks against the real application. They are deliberately about outcomes a
/// member would notice — who gets in, who gets turned away, what a loan does — rather than
/// about implementation details.
/// </summary>
[Collection("portal")]
public class PortalTests
{
    private static readonly Regex CodeBlock = new("<code class=\"break\">(.*?)</code>", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly PortalFactory _factory;

    public PortalTests(PortalFactory factory) => _factory = factory;

    // ---------- helpers ----------

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.NewClient();
        await client.SignInAsync(PortalFactory.AdminEmail, PortalFactory.AdminPassword);
        return client;
    }

    private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@example.test";

    private static readonly Regex IssuedCode = new("id=\"issued-code\">([0-9 ]+)<", RegexOptions.Compiled);

    /// <summary>
    /// Issues an invitation as the admin, as a person would from the Invitations page, and returns the
    /// sign-up link (path and query) together with the six-digit access code shown beside it.
    /// </summary>
    private static async Task<(string Link, string Code)> InviteAsync(HttpClient admin, string email, string role = "Member")
    {
        var response = await admin.PostFormAsync("/Admin/Invitations", "/Admin/Invitations?handler=Issue",
        [
            new("Input.Email", email),
            new("Input.Role", role),
            new("Input.ValidForDays", "7")
        ]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // The code and link are shown once, on the very next page view.
        var html = await admin.GetHtmlAsync("/Admin/Invitations");
        var code = IssuedCode.Match(html);
        Assert.True(code.Success, "The access code was not shown to the admin.");
        var link = CodeBlock.Match(html);
        Assert.True(link.Success, "The invitation link was not shown to the admin.");

        return (new Uri(WebUtility.HtmlDecode(link.Groups[1].Value.Trim())).PathAndQuery, code.Groups[1].Value.Replace(" ", string.Empty));
    }

    private static Task<HttpResponseMessage> TryRegisterAsync(
        HttpClient client, string email, string code, string displayName = "Test Member", string? password = null)
    {
        password ??= PortalFactory.MemberPassword;
        return client.PostFormAsync("/Account/Register", "/Account/Register",
        [
            new("Input.Email", email),
            new("Input.AccessCode", code),
            new("Input.DisplayName", displayName),
            new("Input.Password", password),
            new("Input.ConfirmPassword", password)
        ]);
    }

    private static async Task RegisterAsync(HttpClient client, string email, string code, string displayName = "Test Member")
    {
        var response = await TryRegisterAsync(client, email, code, displayName);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private async Task<(HttpClient Client, string Email)> NewMemberAsync(HttpClient admin, string label)
    {
        var email = UniqueEmail(label);
        var (_, code) = await InviteAsync(admin, email);
        var client = _factory.NewClient();
        await RegisterAsync(client, email, code, label);
        return (client, email);
    }

    private async Task<ApplicationUser> FindUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByEmailAsync(email))!;
    }

    private async Task<bool> IsInRoleAsync(string email, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        return await users.IsInRoleAsync(user, role);
    }

    private static string RedirectTarget(HttpResponseMessage response) => response.Headers.Location?.ToString() ?? string.Empty;

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[64]];

    private static MultipartFormDataContent ToolForm(string token, string name, string fileName, string fileType, byte[] file)
    {
        var photo = new ByteArrayContent(file);
        photo.Headers.ContentType = new MediaTypeHeaderValue(fileType);

        return new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(name), "Input.Name" },
            { new StringContent("Power tools"), "Input.Category" },
            { new StringContent("Takes 18V batteries."), "Input.Description" },
            { new StringContent("Garage"), "Input.PickupLocation" },
            { new StringContent("7"), "Input.MaxLoanDays" },
            { photo, "Photos", fileName }
        };
    }

    // ---------- who gets in ----------

    [Theory]
    [InlineData("/")]
    [InlineData("/Tools/Mine")]
    [InlineData("/Tools/Create")]
    [InlineData("/Bookings")]
    [InlineData("/Admin/Invitations")]
    [InlineData("/Admin/Members")]
    [InlineData("/Account/Manage")]
    [InlineData("/photos/1")]
    public async Task Anonymous_visitors_are_sent_to_sign_in(string url)
    {
        var response = await _factory.NewClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", RedirectTarget(response));
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Register")]
    [InlineData("/Account/ForgotPassword")]
    public async Task The_few_public_pages_are_reachable(string url)
    {
        await _factory.NewClient().GetHtmlAsync(url);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        var response = await _factory.NewClient().GetAsync("/Account/Login");

        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task The_registration_form_asks_for_email_password_and_a_six_digit_access_code()
    {
        var html = await _factory.NewClient().GetHtmlAsync("/Account/Register");

        Assert.Contains("name=\"Input.Email\"", html);
        Assert.Contains("name=\"Input.AccessCode\"", html);
        Assert.Contains("name=\"Input.Password\"", html);
        Assert.Contains("name=\"Input.ConfirmPassword\"", html);
        Assert.Contains("six-digit", html);
    }

    [Fact]
    public async Task The_invitation_link_fills_in_the_email_but_never_the_code()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("prefill");
        var (link, code) = await InviteAsync(admin, email);

        var html = await _factory.NewClient().GetHtmlAsync(link);

        Assert.Contains($"value=\"{email}\"", html);

        // The link names only the address. The code field is empty, and no code travels in the URL.
        Assert.DoesNotContain("code=", link);
        var codeInput = Regex.Match(html, "<input[^>]*name=\"Input.AccessCode\"[^>]*>").Value;
        Assert.NotEmpty(codeInput);
        Assert.DoesNotContain("value=", codeInput);
    }

    [Fact]
    public async Task Registration_without_a_valid_email_and_code_pair_is_refused()
    {
        var client = _factory.NewClient();
        var email = UniqueEmail("sneaky");

        // No invitation at all, and a made-up code.
        var response = await TryRegisterAsync(client, email, "123456", "Sneaky");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("do not match a valid invitation", await response.Content.ReadAsStringAsync());
        Assert.Null(await FindUserAsync(email));
    }

    [Fact]
    public async Task A_missing_or_malformed_access_code_is_refused_and_creates_nothing()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("malformed");
        await InviteAsync(admin, email);

        foreach (var bad in new[] { "", "12345", "1234567", "abcdef", "12 34 5x" })
        {
            var response = await TryRegisterAsync(_factory.NewClient(), email, bad);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(await FindUserAsync(email));
        }
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_address_look_identical()
    {
        async Task<string> Attempt(string email, string password)
        {
            var response = await _factory.NewClient().PostFormAsync("/Account/Login", "/Account/Login",
            [
                new("Input.Email", email),
                new("Input.Password", password)
            ]);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        var wrongPassword = await Attempt(PortalFactory.AdminEmail, "definitely-not-the-password");
        var unknownAddress = await Attempt("nobody@example.test", "definitely-not-the-password");

        Assert.Contains("was not recognised", wrongPassword);
        Assert.Contains("was not recognised", unknownAddress);
    }

    [Fact]
    public async Task The_admin_can_open_every_page()
    {
        var admin = await AdminAsync();

        foreach (var url in new[]
                 {
                     "/", "/?Query=drill&Category=Power%20tools", "/Tools/Mine", "/Tools/Create",
                     "/Bookings", "/Admin/Invitations", "/Admin/Members", "/Account/Manage"
                 })
        {
            await admin.GetHtmlAsync(url);
        }
    }

    // ---------- invitations and access codes ----------

    [Fact]
    public async Task The_admin_is_shown_a_unique_six_digit_code_with_every_invitation()
    {
        var admin = await AdminAsync();

        var codes = new HashSet<string>();
        for (var i = 0; i < 12; i++)
        {
            var (_, code) = await InviteAsync(admin, UniqueEmail($"unique{i}"));
            Assert.Matches("^[0-9]{6}$", code);
            Assert.True(codes.Add(code), $"code {code} was issued twice");
        }
    }

    [Fact]
    public async Task An_invitation_works_once_and_a_member_is_not_an_admin()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("newcomer");
        var (_, code) = await InviteAsync(admin, email);

        var member = _factory.NewClient();
        await RegisterAsync(member, email, code, "Newcomer");

        // Spent: the same email and code now lead nowhere.
        var again = await TryRegisterAsync(_factory.NewClient(), email, code, "Newcomer Again");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("do not match a valid invitation", await again.Content.ReadAsStringAsync());

        // Signed in straight away, but only as a member.
        await member.GetHtmlAsync("/");
        var denied = await member.GetAsync("/Admin/Members");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("AccessDenied", RedirectTarget(denied));

        Assert.True(await IsInRoleAsync(email, "Member"));
        Assert.False(await IsInRoleAsync(email, "Admin"));
    }

    [Fact]
    public async Task The_member_signs_in_afterwards_with_the_password_they_chose()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("chooser");
        var (_, code) = await InviteAsync(admin, email);
        const string chosen = "My-Very-Own-Passphrase-31!";

        var response = await TryRegisterAsync(_factory.NewClient(), email, code, "Chooser", chosen);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        await _factory.NewClient().SignInAsync(email, chosen);
    }

    [Fact]
    public async Task The_code_works_only_with_the_email_it_was_issued_for()
    {
        var admin = await AdminAsync();
        var alice = UniqueEmail("alice");
        var bob = UniqueEmail("bob");
        var (_, aliceCode) = await InviteAsync(admin, alice);
        var (_, bobCode) = await InviteAsync(admin, bob);

        // Alice's code with Bob's address, and the other way round: both refused.
        var swapped = await TryRegisterAsync(_factory.NewClient(), bob, aliceCode, "Mallory");
        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        Assert.Null(await FindUserAsync(bob));

        // Each still works for its owner.
        await RegisterAsync(_factory.NewClient(), alice, aliceCode, "Alice");
        await RegisterAsync(_factory.NewClient(), bob, bobCode, "Bob");
    }

    [Fact]
    public async Task The_code_and_email_are_accepted_however_they_are_typed()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("typing");
        var (_, code) = await InviteAsync(admin, email);

        await RegisterAsync(_factory.NewClient(), email.ToUpperInvariant(), $"{code[..3]} {code[3..]}", "Typist");
    }

    [Fact]
    public async Task Every_kind_of_failure_gets_the_same_answer()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("uniform");
        var (_, code) = await InviteAsync(admin, email);
        var wrong = code == "000000" ? "000001" : "000000";

        async Task<string> Attempt(string e, string c)
        {
            var response = await TryRegisterAsync(_factory.NewClient(), e, c);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            var start = html.IndexOf("do not match", StringComparison.Ordinal);
            Assert.True(start >= 0, "no failure message shown");
            return html.Substring(start, 60);
        }

        var wrongCode = await Attempt(email, wrong);
        var unknownAddress = await Attempt(UniqueEmail("nobody"), code);
        var wrongBoth = await Attempt(UniqueEmail("nobody"), wrong);

        Assert.Equal(wrongCode, unknownAddress);
        Assert.Equal(wrongCode, wrongBoth);
    }

    [Fact]
    public async Task Guessing_codes_locks_the_invitation_even_against_the_right_code()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("guessed");
        var (_, code) = await InviteAsync(admin, email);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < 5; i++)
        {
            await TryRegisterAsync(_factory.NewClient(), email, wrong);
        }

        // The real code is turned away while the pause lasts.
        var response = await TryRegisterAsync(_factory.NewClient(), email, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await FindUserAsync(email));
    }

    [Fact]
    public async Task A_weak_password_does_not_use_up_the_invitation()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("weakpw");
        var (_, code) = await InviteAsync(admin, email);

        var weak = await TryRegisterAsync(_factory.NewClient(), email, code, "Weak", "short");
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
        Assert.Null(await FindUserAsync(email));

        await RegisterAsync(_factory.NewClient(), email, code, "Weak");
    }

    [Fact]
    public async Task The_code_is_stored_only_as_a_keyed_hash()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("hashed");
        var (_, code) = await InviteAsync(admin, email);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ToolShed.Web.Data.ApplicationDbContext>();
        var stored = db.Invitations.Single(i => i.Email == email.ToUpperInvariant());

        Assert.NotEqual(code, stored.CodeHash);
        Assert.DoesNotContain(code, stored.CodeHash);
        Assert.Equal(64, stored.CodeHash.Length);
    }

    [Fact]
    public async Task Inviting_an_existing_member_is_refused()
    {
        var admin = await AdminAsync();
        var (_, email) = await NewMemberAsync(admin, "existing");

        var response = await admin.PostFormAsync("/Admin/Invitations", "/Admin/Invitations?handler=Issue",
        [
            new("Input.Email", email),
            new("Input.Role", "Member"),
            new("Input.ValidForDays", "7")
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("already belongs to a member", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_revoked_invitation_stops_working()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("revoked");
        var (_, code) = await InviteAsync(admin, email);

        var list = await admin.GetHtmlAsync("/Admin/Invitations");
        var id = Regex.Match(list, "name=\"invitationId\" value=\"(\\d+)\"").Groups[1].Value;
        Assert.NotEmpty(id);

        var revoke = await admin.PostFormAsync("/Admin/Invitations", "/Admin/Invitations?handler=Revoke", [new("invitationId", id)]);
        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);

        var response = await TryRegisterAsync(_factory.NewClient(), email, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await FindUserAsync(email));
    }

    [Fact]
    public async Task Re_inviting_an_address_retires_the_earlier_code()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("twice");

        var (_, first) = await InviteAsync(admin, email);
        var (_, second) = await InviteAsync(admin, email);
        Assert.NotEqual(first, second);

        var stale = await TryRegisterAsync(_factory.NewClient(), email, first);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Null(await FindUserAsync(email));

        await RegisterAsync(_factory.NewClient(), email, second);
    }

    // ---------- tools, photos and loans ----------

    [Fact]
    public async Task Members_can_list_a_tool_with_a_photo_and_others_can_borrow_it()
    {
        var admin = await AdminAsync();
        var (owner, ownerEmail) = await NewMemberAsync(admin, "owner");

        // List a tool with a photo.
        var token = await owner.AntiforgeryTokenAsync("/Tools/Create");
        var created = await owner.PostAsync("/Tools/Create", ToolForm(token, "Cordless drill", "drill.png", "image/png", PngBytes));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var toolId = RedirectTarget(created).Split('/').Last();
        var detailsUrl = $"/Tools/Details/{toolId}";

        // The photo is served to members, never to the public.
        var details = await owner.GetHtmlAsync(detailsUrl);
        Assert.Contains("Cordless drill", details);
        var photoId = Regex.Match(details, "src=\"/photos/(\\d+)\"").Groups[1].Value;
        Assert.NotEmpty(photoId);

        var photo = await owner.GetAsync($"/photos/{photoId}");
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal("image/png", photo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Redirect, (await _factory.NewClient().GetAsync($"/photos/{photoId}")).StatusCode);

        // It shows up in the catalogue and in search.
        Assert.Contains("Cordless drill", await admin.GetHtmlAsync("/"));
        Assert.Contains("Cordless drill", await admin.GetHtmlAsync("/?Query=cordless"));
        Assert.DoesNotContain("Cordless drill", await admin.GetHtmlAsync("/?Query=zzzzzz"));

        // Another member asks to borrow it.
        var start = DateOnly.FromDateTime(DateTime.Now).AddDays(3);
        Assert.Contains("Ask to borrow it", await admin.GetHtmlAsync(detailsUrl));

        var request = await admin.PostFormAsync(detailsUrl, detailsUrl,
        [
            new("Input.StartDate", start.ToString("yyyy-MM-dd")),
            new("Input.EndDate", start.AddDays(2).ToString("yyyy-MM-dd")),
            new("Input.Note", "For the shelves")
        ]);
        Assert.Equal(HttpStatusCode.Redirect, request.StatusCode);

        // The owner sees it and approves it.
        var ownerLoans = await owner.GetHtmlAsync("/Bookings");
        Assert.Contains("Requested", ownerLoans);
        var bookingId = Regex.Match(ownerLoans, "name=\"bookingId\" value=\"(\\d+)\"").Groups[1].Value;
        Assert.NotEmpty(bookingId);

        var approve = await owner.PostFormAsync("/Bookings", "/Bookings?handler=Approve",
        [
            new("bookingId", bookingId),
            new("ownerNote", "Mind the battery")
        ]);
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);

        var borrowerLoans = await admin.GetHtmlAsync("/Bookings");
        Assert.Contains("Approved", borrowerLoans);
        Assert.Contains("Mind the battery", borrowerLoans);
        Assert.Contains("day-booked", await admin.GetHtmlAsync(detailsUrl));

        // Overlapping dates are now spoken for.
        var clash = await admin.PostFormAsync(detailsUrl, detailsUrl,
        [
            new("Input.StartDate", start.AddDays(1).ToString("yyyy-MM-dd")),
            new("Input.EndDate", start.AddDays(3).ToString("yyyy-MM-dd")),
            new("Input.Note", "")
        ]);
        Assert.Equal(HttpStatusCode.OK, clash.StatusCode);
        Assert.Contains("already spoken for", await clash.Content.ReadAsStringAsync());

        // Owners cannot borrow their own tools, and nobody else can edit them.
        var selfBooking = await owner.PostFormAsync(detailsUrl, detailsUrl,
        [
            new("Input.StartDate", start.AddDays(10).ToString("yyyy-MM-dd")),
            new("Input.EndDate", start.AddDays(11).ToString("yyyy-MM-dd")),
            new("Input.Note", "")
        ]);
        Assert.Equal(HttpStatusCode.Redirect, selfBooking.StatusCode);
        Assert.Contains("AccessDenied", RedirectTarget(selfBooking));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/Tools/Edit/{toolId}")).StatusCode);

        // Handing it back closes the loan.
        var returned = await owner.PostFormAsync("/Bookings", "/Bookings?handler=Returned", [new("bookingId", bookingId)]);
        Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
        Assert.Contains("Returned", await admin.GetHtmlAsync("/Bookings"));

        Assert.NotNull(await FindUserAsync(ownerEmail));
    }

    [Fact]
    public async Task Uploads_that_are_not_photos_are_rejected()
    {
        var admin = await AdminAsync();
        var (owner, _) = await NewMemberAsync(admin, "uploader");

        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var token = await owner.AntiforgeryTokenAsync("/Tools/Create");
        var response = await owner.PostAsync("/Tools/Create", ToolForm(token, "Sneaky saw", "saw.png", "image/png", svg));

        // Even with a PNG file name and a PNG content type, the bytes decide.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Only JPEG, PNG, GIF and WebP", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Sneaky saw", await owner.GetHtmlAsync("/Tools/Mine"));
    }

    // ---------- looking after members ----------

    [Fact]
    public async Task An_admin_reset_link_lets_a_member_choose_a_new_password_once()
    {
        var admin = await AdminAsync();
        var (_, email) = await NewMemberAsync(admin, "forgetful");
        var user = await FindUserAsync(email);

        var issued = await admin.PostFormAsync("/Admin/Members", "/Admin/Members?handler=ResetLink", [new("userId", user.Id)]);
        Assert.Equal(HttpStatusCode.Redirect, issued.StatusCode);

        var members = await admin.GetHtmlAsync("/Admin/Members");
        var match = CodeBlock.Match(members);
        Assert.True(match.Success, "The reset link was not shown to the admin.");
        var resetUrl = new Uri(WebUtility.HtmlDecode(match.Groups[1].Value.Trim())).PathAndQuery;

        var query = HttpUtility.ParseQueryString(new Uri("https://localhost" + resetUrl).Query);
        const string newPassword = "Brand-New-Password-42!";

        var anonymous = _factory.NewClient();
        Assert.Contains("Choose a new password", await anonymous.GetHtmlAsync(resetUrl));

        var reset = await anonymous.PostFormAsync(resetUrl, resetUrl,
        [
            new("UserId", query["userId"]!),
            new("Code", query["code"]!),
            new("Input.NewPassword", newPassword),
            new("Input.ConfirmPassword", newPassword)
        ]);
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);

        // The new password works and the old one does not.
        await _factory.NewClient().SignInAsync(email, newPassword);
        var old = await _factory.NewClient().PostFormAsync("/Account/Login", "/Account/Login",
        [
            new("Input.Email", email),
            new("Input.Password", PortalFactory.MemberPassword)
        ]);
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);

        // The link is spent.
        var again = await anonymous.PostFormAsync("/Account/Login", resetUrl,
        [
            new("UserId", query["userId"]!),
            new("Code", query["code"]!),
            new("Input.NewPassword", "Yet-Another-Password-77!"),
            new("Input.ConfirmPassword", "Yet-Another-Password-77!")
        ]);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("not valid", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_weak_password_is_refused_on_reset()
    {
        var admin = await AdminAsync();
        var (_, email) = await NewMemberAsync(admin, "weak");
        var user = await FindUserAsync(email);

        await admin.PostFormAsync("/Admin/Members", "/Admin/Members?handler=ResetLink", [new("userId", user.Id)]);
        var match = CodeBlock.Match(await admin.GetHtmlAsync("/Admin/Members"));
        var resetUrl = new Uri(WebUtility.HtmlDecode(match.Groups[1].Value.Trim())).PathAndQuery;
        var query = HttpUtility.ParseQueryString(new Uri("https://localhost" + resetUrl).Query);

        var response = await _factory.NewClient().PostFormAsync(resetUrl, resetUrl,
        [
            new("UserId", query["userId"]!),
            new("Code", query["code"]!),
            new("Input.NewPassword", "short"),
            new("Input.ConfirmPassword", "short")
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Forgot_password_explains_when_email_is_not_set_up()
    {
        var html = await _factory.NewClient().GetHtmlAsync("/Account/ForgotPassword");
        Assert.Contains("not set up to send email", html);
    }

    [Fact]
    public async Task Admins_can_change_other_members_roles_but_not_their_own()
    {
        var admin = await AdminAsync();
        var (_, email) = await NewMemberAsync(admin, "promotee");
        var member = await FindUserAsync(email);
        var self = await FindUserAsync(PortalFactory.AdminEmail);

        await admin.PostFormAsync("/Admin/Members", "/Admin/Members?handler=SetRole", [new("userId", member.Id), new("role", "Admin")]);
        Assert.True(await IsInRoleAsync(email, "Admin"));
        Assert.False(await IsInRoleAsync(email, "Member"));

        await admin.PostFormAsync("/Admin/Members", "/Admin/Members?handler=SetRole", [new("userId", member.Id), new("role", "Member")]);
        Assert.False(await IsInRoleAsync(email, "Admin"));

        // Changing your own role would risk leaving the portal with no admin at all.
        await admin.PostFormAsync("/Admin/Members", "/Admin/Members?handler=SetRole", [new("userId", self.Id), new("role", "Member")]);
        Assert.True(await IsInRoleAsync(PortalFactory.AdminEmail, "Admin"));

        // Only real roles are accepted.
        var bogus = await admin.PostFormAsync("/Admin/Members", "/Admin/Members?handler=SetRole", [new("userId", member.Id), new("role", "Superuser")]);
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
    }

    [Fact]
    public async Task A_suspended_member_cannot_sign_in_until_restored()
    {
        var admin = await AdminAsync();
        var (_, email) = await NewMemberAsync(admin, "suspended");
        var user = await FindUserAsync(email);

        await admin.PostFormAsync("/Admin/Members", "/Admin/Members?handler=Toggle", [new("userId", user.Id)]);

        var blocked = await _factory.NewClient().PostFormAsync("/Account/Login", "/Account/Login",
        [
            new("Input.Email", email),
            new("Input.Password", PortalFactory.MemberPassword)
        ]);
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        Assert.Contains("locked", await blocked.Content.ReadAsStringAsync());

        await admin.PostFormAsync("/Admin/Members", "/Admin/Members?handler=Toggle", [new("userId", user.Id)]);
        await _factory.NewClient().SignInAsync(email, PortalFactory.MemberPassword);
    }
}
