using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Security;

public sealed record RecoveryTicket(long AccountId, DateTime ExpiresAt, string CredentialFingerprint, string Nonce);

public sealed class RecoveryTokenService(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("MyShoppingList.AccountRecovery.v1");
    public static string Fingerprint(UserAccount user) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(user.PasswordHash + ":" + user.PasswordSalt)));
    public string Create(UserAccount user, DateTime expiresAt) => protector.Protect(JsonSerializer.Serialize(
        new RecoveryTicket(user.Id, expiresAt, Fingerprint(user), SessionToken.Create())));
    public RecoveryTicket? Read(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048) return null;
        try
        {
            var ticket = JsonSerializer.Deserialize<RecoveryTicket>(protector.Unprotect(value));
            return ticket is { AccountId: > 0, CredentialFingerprint.Length: 64 } && SessionToken.IsValidFormat(ticket.Nonce)
                && ticket.ExpiresAt > clock.GetUtcNow().UtcDateTime && ticket.ExpiresAt <= clock.GetUtcNow().UtcDateTime.AddMinutes(15)
                ? ticket : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { return null; }
    }
}
