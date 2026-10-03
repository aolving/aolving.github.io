using ToolShed.Client;
using Xunit;

namespace ToolShed.Tests;

public class ClientHelperTests
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE";

    [Theory]
    [InlineData("https://tools.example.com/Account/Register?token=" + Token, Token)]
    [InlineData("  https://tools.example.com/Account/Register?token=" + Token + "  ", Token)]
    [InlineData("https://tools.example.com/Account/Register?x=1&TOKEN=" + Token, Token)]
    [InlineData(Token, Token)]
    [InlineData("  " + Token + "\n", Token)]
    public void An_invitation_token_is_found_in_a_link_or_pasted_bare(string pasted, string expected)
    {
        Assert.Equal(expected, InviteLink.ExtractToken(pasted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("https://tools.example.com/Account/Register")]
    [InlineData("https://tools.example.com/Account/Register?token=")]
    [InlineData("https://tools.example.com/?token=has spaces and symbols!!")]
    public void Anything_else_is_rejected_before_it_reaches_the_server(string? pasted)
    {
        Assert.Null(InviteLink.ExtractToken(pasted));
    }

    [Theory]
    [InlineData("tools.example.com", "https://tools.example.com/")]
    [InlineData("  tools.example.com/  ", "https://tools.example.com/")]
    [InlineData("https://tools.example.com", "https://tools.example.com/")]
    [InlineData("https://tools.example.com:8443/shed", "https://tools.example.com:8443/shed/")]
    [InlineData("tools.example.com/?x=1#frag", "https://tools.example.com/")]
    [InlineData("http://localhost:5181", "http://localhost:5181/")]
    [InlineData("http://10.0.2.2:5181", "http://10.0.2.2:5181/")]
    public void Server_addresses_are_normalised(string typed, string expected)
    {
        Assert.True(ServerAddress.TryNormalise(typed, out var address, out var error), error);
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a web address")]
    [InlineData("http://tools.example.com")]
    [InlineData("http://192.168.1.20")]
    [InlineData("ftp://tools.example.com")]
    public void Unusable_or_unencrypted_server_addresses_are_refused(string? typed)
    {
        Assert.False(ServerAddress.TryNormalise(typed, out _, out var error));
        Assert.NotEmpty(error);
    }
}
