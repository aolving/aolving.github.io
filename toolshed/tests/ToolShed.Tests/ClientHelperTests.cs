using ToolShed.Client;
using Xunit;

namespace ToolShed.Tests;

public class ClientHelperTests
{
    [Theory]
    [InlineData("123456", "123456")]
    [InlineData("123 456", "123456")]
    [InlineData("123-456", "123456")]
    [InlineData("  123 456 \n", "123456")]
    [InlineData("000 000", "000000")]
    public void An_access_code_is_found_however_it_was_typed(string typed, string expected)
    {
        Assert.Equal(expected, AccessCode.Normalise(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 34 5a")]
    [InlineData("abcdef")]
    [InlineData("123.456")]
    [InlineData("123456; DROP TABLE Invitations")]
    public void Anything_that_is_not_six_digits_is_rejected_before_it_reaches_the_server(string? typed)
    {
        Assert.Null(AccessCode.Normalise(typed));
    }

    [Fact]
    public void Codes_are_shown_in_two_groups_of_three()
    {
        Assert.Equal("123 456", AccessCode.Format("123456"));
        Assert.Equal("007 001", AccessCode.Format("007001"));
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
