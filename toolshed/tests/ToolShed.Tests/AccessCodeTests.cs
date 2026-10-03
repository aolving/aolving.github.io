using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

public class AccessCodeTests
{
    [Fact]
    public void Generated_codes_are_always_exactly_six_digits()
    {
        for (var i = 0; i < 2000; i++)
        {
            var code = AccessCodes.Generate();
            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
        }
    }

    [Fact]
    public void Generated_codes_use_the_whole_range_including_leading_zeros()
    {
        var codes = Enumerable.Range(0, 5000).Select(_ => AccessCodes.Generate()).ToList();

        Assert.Contains(codes, c => c.StartsWith('0'));
        Assert.Contains(codes, c => c.StartsWith('9'));
        // Five thousand draws from a million should almost never repeat; a stuck generator would.
        Assert.True(codes.Distinct().Count() > 4900);
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123 456 ", "123456")]
    [InlineData("123-456", "123456")]
    public void Normalising_accepts_the_ways_people_type_a_code(string typed, string expected)
    {
        Assert.True(AccessCodes.TryNormalise(typed, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("'; --")]
    public void Normalising_rejects_everything_else(string? typed)
    {
        Assert.False(AccessCodes.TryNormalise(typed, out _));
    }

    [Fact]
    public void Hashes_depend_on_the_secret_so_a_stolen_database_cannot_be_reversed_offline()
    {
        var a = new AccessCodeHasher(Enumerable.Repeat((byte)1, 32).ToArray());
        var b = new AccessCodeHasher(Enumerable.Repeat((byte)2, 32).ToArray());

        Assert.Equal(a.Hash("123456"), a.Hash("123456"));
        Assert.NotEqual(a.Hash("123456"), a.Hash("123457"));
        Assert.NotEqual(a.Hash("123456"), b.Hash("123456"));
        Assert.DoesNotContain("123456", a.Hash("123456"));
        Assert.True(a.Matches(a.Hash("123456"), "123456"));
        Assert.False(a.Matches(a.Hash("123456"), "654321"));
    }

    [Fact]
    public void The_secret_is_created_once_and_reused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "toolshed-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = AccessCodeHasher.FromDirectory(dir);
            var second = AccessCodeHasher.FromDirectory(dir);

            Assert.Equal(first.Hash("123456"), second.Hash("123456"));
            Assert.Equal(32, File.ReadAllBytes(Path.Combine(dir, "access-code.key")).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Short_secrets_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new AccessCodeHasher(new byte[8]));
    }
}
