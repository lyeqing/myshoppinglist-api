using System.Security.Cryptography;

namespace myshoppinglist_api.Security;

public static class PasswordHasher
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const string Prefix = "pbkdf2-sha256$600000$";
    public static (string Hash, string Salt) Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return (Prefix + Convert.ToBase64String(Derive(password, salt)), Convert.ToBase64String(salt));
    }
    public static bool Verify(string? password, string? hash, string? salt)
    {
        if (password is null || password.Length is < 1 or > 1024 || hash is null || salt is null
            || hash.Length != Prefix.Length + 44 || salt.Length != 24 || !hash.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var expected = Convert.FromBase64String(hash[Prefix.Length..]);
            var bytes = Convert.FromBase64String(salt);
            return expected.Length == KeyBytes && bytes.Length == SaltBytes
                && CryptographicOperations.FixedTimeEquals(Derive(password, bytes), expected);
        }
        catch (FormatException) { return false; }
    }
    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
}
