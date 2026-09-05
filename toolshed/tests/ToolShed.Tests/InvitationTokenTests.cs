using ToolShed.Web.Models;
using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

public class InvitationTokenTests
{
    [Fact]
    public void Tokens_are_url_safe_and_unique()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => TokenGenerator.NewToken()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct().Count());
        Assert.All(tokens, token =>
        {
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
            Assert.True(token.Length >= 40);
        });
    }

    [Fact]
    public void Hashing_is_stable_and_does_not_reveal_the_token()
    {
        var token = TokenGenerator.NewToken();
        var hash = TokenGenerator.Hash(token);

        Assert.Equal(hash, TokenGenerator.Hash(token));
        Assert.NotEqual(hash, TokenGenerator.Hash(TokenGenerator.NewToken()));
        Assert.DoesNotContain(token, hash);
    }

    [Fact]
    public void Generated_bootstrap_passwords_meet_the_portal_policy()
    {
        var password = TokenGenerator.NewPassword();

        Assert.True(password.Length >= 12);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsDigit);
        Assert.Contains(password, c => !char.IsLetterOrDigit(c));
    }

    [Fact]
    public void An_invitation_is_usable_only_while_it_is_fresh_unspent_and_unrevoked()
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        Invitation Fresh() => new() { Email = "SOMEONE@EXAMPLE.COM", ExpiresUtc = now.AddDays(7) };

        Assert.True(Fresh().IsUsableAt(now));

        var expired = Fresh();
        expired.ExpiresUtc = now.AddSeconds(-1);
        Assert.False(expired.IsUsableAt(now));

        var redeemed = Fresh();
        redeemed.RedeemedUtc = now;
        Assert.False(redeemed.IsUsableAt(now));

        var revoked = Fresh();
        revoked.RevokedUtc = now;
        Assert.False(revoked.IsUsableAt(now));
    }
}
