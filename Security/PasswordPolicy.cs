using System.Text;

namespace myshoppinglist_api.Security;

public static class PasswordPolicy
{
    public static string? Error(string? password, int minimum)
    {
        if (!string.IsNullOrWhiteSpace(password) && password.Length >= minimum && password.Length <= 1024
            && password.Any(c => c is >= '0' and <= '9') && password.Any(c => c is >= 'a' and <= 'z')
            && password.Any(c => c is >= 'A' and <= 'Z')
            && password.EnumerateRunes().Any(c => Rune.IsPunctuation(c) || Rune.IsSymbol(c))) return null;
        return $"Use {minimum}–1024 characters with at least one number (0–9), one lowercase letter (a–z), one uppercase letter (A–Z), and one special character. Spaces do not count as special characters.";
    }
}
