using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Passwords_use_unique_salts_and_verify_without_trimming()
    {
        const string password = " long passphrase with spaces ";
        var first = PasswordHasher.Hash(password); var second = PasswordHasher.Hash(password);
        Assert.NotEqual(first.Salt, second.Salt); Assert.NotEqual(first.Hash, second.Hash);
        Assert.StartsWith("pbkdf2-sha256$600000$", first.Hash);
        Assert.True(PasswordHasher.Verify(password, first.Hash, first.Salt));
        Assert.False(PasswordHasher.Verify(password.Trim(), first.Hash, first.Salt));
        Assert.False(PasswordHasher.Verify("wrong password", first.Hash, first.Salt));
        Assert.False(PasswordHasher.Verify(password, first.Hash, second.Salt));
    }
    [Fact]
    public void Malformed_credentials_and_oversized_passwords_are_rejected()
    {
        var value = PasswordHasher.Hash("A sufficiently long password");
        Assert.False(PasswordHasher.Verify(null, value.Hash, value.Salt));
        Assert.False(PasswordHasher.Verify("", value.Hash, value.Salt));
        Assert.False(PasswordHasher.Verify(new string('x', 1025), value.Hash, value.Salt));
        Assert.False(PasswordHasher.Verify("password", "invalid", value.Salt));
        Assert.False(PasswordHasher.Verify("password", value.Hash, "!".PadRight(24, '!')));
        Assert.False(PasswordHasher.Verify("password", value.Hash.Replace("600000", "999999"), value.Salt));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordHasher.Hash(new string('x', 1025)));
    }
}
