using System.Security.Cryptography;
using System.Text;

namespace myshoppinglist_api.Security;

public static class SessionToken
{
    public static string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static bool IsValidFormat(string? token) => token is { Length: 64 }
        && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Hash(string token)
    {
        if (!IsValidFormat(token)) throw new ArgumentException("Invalid session token format.", nameof(token));
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();
    }
}
