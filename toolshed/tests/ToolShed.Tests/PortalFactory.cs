using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ToolShed.Tests;

/// <summary>
/// Hosts the real portal — Identity, policies, SQLite, the lot — against a throwaway directory,
/// so these tests prove the wiring that the unit tests cannot see.
/// </summary>
public sealed class PortalFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@example.test";
    public const string AdminPassword = "Correct-Horse-9-Battery";
    public const string MemberPassword = "Another-Strong-Pass-7!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "toolshed-tests-" + Guid.NewGuid().ToString("N"));

    public PortalFactory()
    {
        Directory.CreateDirectory(_root);

        // Environment variables are read when the host builder is created, which is the one
        // moment Program.cs consults configuration, so they are the dependable way to point
        // the app at a scratch database and photo folder.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", $"Data Source={Path.Combine(_root, "toolshed.db")}");
        Environment.SetEnvironmentVariable("Storage__PhotoRoot", Path.Combine(_root, "photos"));
        Environment.SetEnvironmentVariable("Storage__KeysDirectory", Path.Combine(_root, "keys"));
        Environment.SetEnvironmentVariable("Seed__AdminEmail", AdminEmail);
        Environment.SetEnvironmentVariable("Seed__AdminPassword", AdminPassword);
        Environment.SetEnvironmentVariable("RateLimits__AuthPermits", "10000");
        // Every request from the test host shares one address, so the general limit would otherwise trip.
        Environment.SetEnvironmentVariable("RateLimits__GeneralPermits", "1000000");
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A locked SQLite file on a CI runner is not worth failing the run over.
            }
        }
    }
}

[CollectionDefinition("portal")]
public class PortalCollection : ICollectionFixture<PortalFactory>
{
}

public static class PortalHttp
{
    private static readonly Regex TokenInput = new("<input[^>]*__RequestVerificationToken[^>]*>", RegexOptions.Compiled);

    private static readonly Regex ValueAttribute = new("value=\"([^\"]+)\"", RegexOptions.Compiled);

    public static async Task<string> GetHtmlAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<string> AntiforgeryTokenAsync(this HttpClient client, string pageUrl)
    {
        var html = await client.GetHtmlAsync(pageUrl);
        var input = TokenInput.Match(html);
        Assert.True(input.Success, $"No antiforgery token on {pageUrl}");

        var value = ValueAttribute.Match(input.Value);
        Assert.True(value.Success, $"Antiforgery input on {pageUrl} has no value");
        return System.Net.WebUtility.HtmlDecode(value.Groups[1].Value);
    }

    public static async Task<HttpResponseMessage> PostFormAsync(
        this HttpClient client, string tokenPage, string postUrl, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var token = await client.AntiforgeryTokenAsync(tokenPage);
        var form = new List<KeyValuePair<string, string>>(fields)
        {
            new("__RequestVerificationToken", token)
        };

        return await client.PostAsync(postUrl, new FormUrlEncodedContent(form));
    }

    public static async Task SignInAsync(this HttpClient client, string email, string password)
    {
        var response = await client.PostFormAsync("/Account/Login", "/Account/Login",
        [
            new("Input.Email", email),
            new("Input.Password", password)
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
