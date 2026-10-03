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

    private static readonly Regex IssuedCodeRow = new("data-for=\"([^\"]+)\">([0-9 ]+)<", RegexOptions.Compiled);

    /// <summary>
    /// Drives the code generator as an admin would and returns what it shows: each code with who it is
    /// for ("open" for an open code). The codes appear only in this one response.
    /// </summary>
    private static async Task<(string Html, List<(string For, string Code)> Codes)> GenerateAsync(
        HttpClient admin, string emails, int openCount, string role = "Member", string handler = "Issue", string? label = null)
    {
        var response = await admin.PostFormAsync("/Admin/Invitations", $"/Admin/Invitations?handler={handler}",
        [
            new("Input.Emails", emails),
            new("Input.OpenCount", openCount.ToString()),
            new("Input.Role", role),
            new("Input.ValidForDays", "7"),
            new("Input.Label", label ?? string.Empty)
        ]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        var codes = IssuedCodeRow.Matches(html)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value.Replace(" ", string.Empty)))
            .ToList();
        return (html, codes);
    }

    /// <summary>One code for one address, as the old "invite someone" form did. Returns the link and the code.</summary>
    private static async Task<(string Link, string Code)> InviteAsync(HttpClient admin, string email, string role = "Member")
    {
        var (html, codes) = await GenerateAsync(admin, email, 0, role);
        var code = codes.SingleOrDefault(c => c.For == email.ToLowerInvariant());
        Assert.False(string.IsNullOrEmpty(code.Code), "The access code was not shown to the admin.");
        var link = CodeBlock.Match(html);
        Assert.True(link.Success, "The invitation link was not shown to the admin.");

        return (new Uri(WebUtility.HtmlDecode(link.Groups[1].Value.Trim())).PathAndQuery, code.Code);
    }

    private static async Task<List<string>> OpenCodesAsync(HttpClient admin, int count)
    {
        var (_, codes) = await GenerateAsync(admin, string.Empty, count);
        return codes.Where(c => c.For == "open").Select(c => c.Code).ToList();
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
        Assert.DoesNotMatch("value=\"[^\"]+\"", codeInput);   // empty (value=\"\") is fine; anything filled in is not
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
            new("Input.Emails", email),
            new("Input.OpenCount", "0"),
            new("Input.Role", "Member"),
            new("Input.ValidForDays", "7")
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("already belong to members", await response.Content.ReadAsStringAsync());
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

    // ---------- the code generator and open codes ----------

    private async Task ResumeOpenCodesAsync(HttpClient admin)
    {
        var response = await admin.PostFormAsync("/Admin/Invitations", "/Admin/Invitations?handler=Resume", []);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task The_generator_makes_a_mixed_batch_of_unique_codes_in_one_go()
    {
        var admin = await AdminAsync();
        var a = UniqueEmail("batch-a");
        var b = UniqueEmail("batch-b");

        var (html, codes) = await GenerateAsync(admin, $"{a}\n{b}", 3, label: "Street party");

        Assert.Equal(5, codes.Count);
        Assert.Equal(5, codes.Select(c => c.Code).Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[0-9]{6}$", c.Code));
        Assert.Contains(codes, c => c.For == a.ToLowerInvariant());
        Assert.Contains(codes, c => c.For == b.ToLowerInvariant());
        Assert.Equal(3, codes.Count(c => c.For == "open"));
        Assert.Contains("5 code(s) generated", html);
    }

    [Fact]
    public async Task The_generator_response_is_never_cached()
    {
        var admin = await AdminAsync();
        var response = await admin.PostFormAsync("/Admin/Invitations", "/Admin/Invitations?handler=Issue",
        [
            new("Input.Emails", string.Empty), new("Input.OpenCount", "1"), new("Input.Role", "Member"), new("Input.ValidForDays", "7")
        ]);

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Someone_joins_with_an_open_code_and_an_email_they_choose()
    {
        var admin = await AdminAsync();
        var code = (await OpenCodesAsync(admin, 1)).Single();
        var chosen = UniqueEmail("chosen");

        var client = _factory.NewClient();
        await RegisterAsync(client, chosen, code, "Open Joiner");

        await client.GetHtmlAsync("/");
        Assert.NotNull(await FindUserAsync(chosen));
        Assert.True(await IsInRoleAsync(chosen, "Member"));
        Assert.False(await IsInRoleAsync(chosen, "Admin"));

        // Spent: nobody else can use it, with this address or another.
        var again = await TryRegisterAsync(_factory.NewClient(), UniqueEmail("latecomer"), code, "Latecomer");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("do not match a valid invitation", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_open_code_can_never_create_an_admin_even_if_the_form_asks_for_one()
    {
        var admin = await AdminAsync();
        var (_, codes) = await GenerateAsync(admin, string.Empty, 1, role: "Admin");
        var chosen = UniqueEmail("sneaky-admin");

        await RegisterAsync(_factory.NewClient(), chosen, codes.Single(c => c.For == "open").Code, "Not An Admin");

        Assert.True(await IsInRoleAsync(chosen, "Member"));
        Assert.False(await IsInRoleAsync(chosen, "Admin"));
    }

    [Fact]
    public async Task An_address_with_its_own_code_cannot_use_an_open_code()
    {
        var admin = await AdminAsync();
        var invited = UniqueEmail("invited");
        var (_, tiedCode) = await InviteAsync(admin, invited);
        var open = (await OpenCodesAsync(admin, 1)).Single();

        var response = await TryRegisterAsync(_factory.NewClient(), invited, open, "Mixer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("do not match a valid invitation", await response.Content.ReadAsStringAsync());
        Assert.Null(await FindUserAsync(invited));
        // Its own code still works.
        await RegisterAsync(_factory.NewClient(), invited, tiedCode, "Invited");
    }

    [Fact]
    public async Task An_open_code_is_not_used_up_by_an_address_that_already_has_an_account()
    {
        var admin = await AdminAsync();
        var (_, existing) = await NewMemberAsync(admin, "already");
        var code = (await OpenCodesAsync(admin, 1)).Single();

        var response = await TryRegisterAsync(_factory.NewClient(), existing, code, "Duplicate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("already exists", await response.Content.ReadAsStringAsync());

        // The code is still good for a different address.
        await RegisterAsync(_factory.NewClient(), UniqueEmail("fresh"), code, "Fresh");
    }

    [Fact]
    public async Task Guessing_open_codes_trips_the_breaker_until_an_admin_resumes_them()
    {
        var admin = await AdminAsync();
        await ResumeOpenCodesAsync(admin);              // start from a clean slate
        var code = (await OpenCodesAsync(admin, 1)).Single();
        var wrong = code == "000000" ? "000001" : "000000";

        try
        {
            for (var i = 0; i < 30; i++)
            {
                await TryRegisterAsync(_factory.NewClient(), UniqueEmail("guess"), wrong, "Guesser");
            }

            // Paused: the real code is turned away too, and the admin is told.
            var joiner = UniqueEmail("honest");
            var blocked = await TryRegisterAsync(_factory.NewClient(), joiner, code, "Honest");
            Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
            Assert.Null(await FindUserAsync(joiner));
            Assert.Contains("Open codes are paused", await admin.GetHtmlAsync("/Admin/Invitations"));
        }
        finally
        {
            await ResumeOpenCodesAsync(admin);
        }

        // Resumed: the same code works for the person it was meant for.
        await RegisterAsync(_factory.NewClient(), UniqueEmail("honest-again"), code, "Honest");
        Assert.DoesNotContain("Open codes are paused", await admin.GetHtmlAsync("/Admin/Invitations"));
    }

    [Fact]
    public async Task The_pause_does_not_stop_codes_tied_to_an_email()
    {
        var admin = await AdminAsync();
        await ResumeOpenCodesAsync(admin);
        var invited = UniqueEmail("tied-during-pause");
        var (_, code) = await InviteAsync(admin, invited);

        try
        {
            for (var i = 0; i < 30; i++)
            {
                await TryRegisterAsync(_factory.NewClient(), UniqueEmail("guess"), "123456", "Guesser");
            }

            await RegisterAsync(_factory.NewClient(), invited, code, "Invited");
        }
        finally
        {
            await ResumeOpenCodesAsync(admin);
        }
    }

    [Fact]
    public async Task The_csv_download_has_the_codes_and_defuses_spreadsheet_formulas()
    {
        var admin = await AdminAsync();
        var email = UniqueEmail("csv");

        var token = await admin.AntiforgeryTokenAsync("/Admin/Invitations");
        var response = await admin.PostAsync("/Admin/Invitations?handler=IssueCsv", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("Input.Emails", email),
            new KeyValuePair<string, string>("Input.OpenCount", "2"),
            new KeyValuePair<string, string>("Input.Role", "Member"),
            new KeyValuePair<string, string>("Input.ValidForDays", "7"),
            new KeyValuePair<string, string>("Input.Label", "=HYPERLINK(\"http://evil.test\",\"click\")"),
            new KeyValuePair<string, string>("__RequestVerificationToken", token)
        ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);

        var csv = System.Text.Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()).TrimStart('\uFEFF');
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Access code,For,Role,Expires,Label,Link", lines[0]);
        Assert.Equal(4, lines.Length);                       // header + 1 emailed + 2 open
        Assert.Matches("^[0-9]{3} [0-9]{3},", lines[1]);     // grouped, so Excel keeps leading zeros
        Assert.Contains(email.ToLowerInvariant(), csv);
        Assert.Contains("Open code (any email)", csv);
        // The label began with '=', which a spreadsheet would run as a formula.
        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.DoesNotContain("\"=HYPERLINK", csv);
        Assert.Contains("'=HYPERLINK", csv);
    }

    [Fact]
    public async Task The_generator_refuses_bad_input_with_a_reason_and_issues_nothing()
    {
        var admin = await AdminAsync();

        async Task<string> Refused(string emails, int open)
        {
            var response = await admin.PostFormAsync("/Admin/Invitations", "/Admin/Invitations?handler=Issue",
            [
                new("Input.Emails", emails), new("Input.OpenCount", open.ToString()), new("Input.Role", "Member"), new("Input.ValidForDays", "7")
            ]);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("code(s) generated", html);
            return html;
        }

        Assert.Contains("Enter at least one email address", await Refused(string.Empty, 0));
        Assert.Contains("do not look like email addresses", await Refused("not an address", 0));
        Assert.Contains("up to 50", await Refused(string.Empty, 51));

        var tooMany = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"bulk{i}-{Guid.NewGuid():N}@example.test"));
        Assert.Contains("up to 50", await Refused(tooMany, 20));
    }

    [Fact]
    public async Task Only_admins_can_use_the_generator()
    {
        var admin = await AdminAsync();
        var (member, _) = await NewMemberAsync(admin, "nosy");

        var response = await member.PostFormAsync("/Account/Manage", "/Admin/Invitations?handler=Issue",
        [
            new("Input.Emails", string.Empty), new("Input.OpenCount", "5"), new("Input.Role", "Member"), new("Input.ValidForDays", "7")
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", RedirectTarget(response));
    }

    [Fact]
    public async Task The_list_shows_open_codes_with_their_notes_and_lets_an_admin_revoke_them()
    {
        var admin = await AdminAsync();
        var note = $"note-{Guid.NewGuid():N}";
        var code = (await GenerateAsync(admin, string.Empty, 1, label: note)).Codes.Single().Code;

        var list = await admin.GetHtmlAsync("/Admin/Invitations");
        Assert.Contains(note, list);
        Assert.Contains("Open code", list);

        var id = Regex.Match(list, "name=\"invitationId\" value=\"(\\d+)\"").Groups[1].Value;
        await admin.PostFormAsync("/Admin/Invitations", "/Admin/Invitations?handler=Revoke", [new("invitationId", id)]);

        var response = await TryRegisterAsync(_factory.NewClient(), UniqueEmail("late"), code, "Late");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("do not match a valid invitation", await response.Content.ReadAsStringAsync());
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
    public async Task The_tool_page_gives_the_calendar_everything_it_needs_and_stops_owners_booking()
    {
        var admin = await AdminAsync();
        var (owner, _) = await NewMemberAsync(admin, "calendar-owner");

        var token = await owner.AntiforgeryTokenAsync("/Tools/Create");
        var created = await owner.PostAsync("/Tools/Create", ToolForm(token, "Calendar drill", "drill.png", "image/png", PngBytes));
        var toolId = RedirectTarget(created).Split('/').Last();
        var detailsUrl = $"/Tools/Details/{toolId}";

        // A member who could borrow it gets the calendar's hooks and the fields it drives.
        var html = await admin.GetHtmlAsync(detailsUrl);
        Assert.Contains("data-booking-calendar", html);
        Assert.Contains("data-max-days=\"7\"", html);
        Assert.Matches("data-today=\"\\d{4}-\\d{2}-\\d{2}\"", html);
        Assert.Contains("data-start-input=\"Input_StartDate\"", html);
        Assert.Contains("data-end-input=\"Input_EndDate\"", html);
        Assert.Contains("id=\"Input_StartDate\"", html);
        Assert.Contains("id=\"Input_EndDate\"", html);
        Assert.Contains("data-booking-submit", html);
        Assert.Matches("<script src=\"/js/booking-calendar\\.js\\?v=", html);

        // Without scripts the plain fields are still there and still enforce the first date as the start.
        Assert.Contains("The return date cannot be before the collection date", html);

        // The owner cannot book their own tool, so there is no calendar for them.
        Assert.DoesNotContain("data-booking-calendar", await owner.GetHtmlAsync(detailsUrl));

        // Once someone has booked it, the dates that are taken are handed to the calendar so it can refuse them.
        var start = DateOnly.FromDateTime(DateTime.Now).AddDays(6);
        await admin.PostFormAsync(detailsUrl, detailsUrl,
        [
            new("Input.StartDate", start.ToString("yyyy-MM-dd")),
            new("Input.EndDate", start.AddDays(1).ToString("yyyy-MM-dd")),
            new("Input.Note", string.Empty)
        ]);
        var (other, _) = await NewMemberAsync(admin, "calendar-other");
        var otherHtml = await other.GetHtmlAsync(detailsUrl);
        var held = Regex.Match(otherHtml, "data-held=\"([^\"]*)\"").Groups[1].Value;
        Assert.Contains($"[\"{start:yyyy-MM-dd}\",\"{start.AddDays(1):yyyy-MM-dd}\",0]", WebUtility.HtmlDecode(held));
    }

    [Fact]
    public async Task The_calendar_script_is_served_as_a_plain_file_and_allowed_by_the_content_security_policy()
    {
        var client = _factory.NewClient();

        var response = await client.GetAsync("/js/booking-calendar.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("createSelection", await response.Content.ReadAsStringAsync());
        // Same-origin scripts only, with no inline script: the calendar ships as its own file for that reason.
        var csp = (await client.GetAsync("/Account/Login")).Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
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
