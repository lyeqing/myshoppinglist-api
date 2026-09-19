using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

public class SessionTokenTests
{
    [Fact]
    public void Tokens_are_random_fixed_length_and_hashes_are_deterministic()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => SessionToken.Create()).ToArray();
        Assert.Equal(100, tokens.Distinct().Count());
        Assert.All(tokens, token =>
        {
            Assert.True(SessionToken.IsValidFormat(token));
            Assert.Equal(64, SessionToken.Hash(token).Length);
            Assert.NotEqual(token, SessionToken.Hash(token));
            Assert.Equal(SessionToken.Hash(token), SessionToken.Hash(token));
        });
        Assert.NotEqual(SessionToken.Hash(tokens[0]), SessionToken.Hash(tokens[1]));
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000 ")]
    public void Invalid_tokens_are_rejected_before_hashing(string? token)
    {
        Assert.False(SessionToken.IsValidFormat(token));
        Assert.Throws<ArgumentException>(() => SessionToken.Hash(token!));
    }
}
