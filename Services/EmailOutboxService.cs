using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public sealed class EmailOutboxService(MyShoppingListDbContext db, IDataProtectionProvider protection,
    IOptions<EmailDeliveryOptions> delivery, IOptions<AccountRecoveryOptions> recovery, RecoveryTokenService tokens,
    IEmailTransport transport, TimeProvider clock, ILogger<EmailOutboxService> logger)
{
    private readonly IDataProtector protector = protection.CreateProtector("MyShoppingList.EmailOutbox.v1");
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    public static string AddressHash(string email) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email)));

    public EmailOutboxMessage Add(string kind, OutboundEmail message, DateTime expires, Guid? id = null)
    {
        if (kind is not ("account_recovery" or "password_changed" or "security_alert") || message.Recipients.Length is < 1 or > 5
            || message.Recipients.Any(r => AccountAuthService.NormaliseEmail(r) is null)
            || message.Subject.Length > 200 || message.Subject.Any(char.IsControl) || message.Body.Length > 12000)
            throw new InvalidOperationException("Invalid fixed email contract.");
        var row = new EmailOutboxMessage { Id = id ?? Guid.NewGuid(), Kind = kind, CreatedAt = Now, NextAttemptAt = Now,
            ExpiresAt = expires, RecipientHash = kind == "account_recovery" ? AddressHash(message.Recipients[0]) : null,
            ProtectedPayload = protector.Protect(JsonSerializer.Serialize(message)) };
        db.EmailOutboxMessages.Add(row);
        return row;
    }

    public async Task RequestRecoveryAsync(string? value, CancellationToken ct)
    {
        if (!recovery.Value.Enabled || !delivery.Value.Enabled) throw new UserImportException(503, "Password recovery is not available. Contact support for help.");
        var email = AccountAuthService.NormaliseEmail(value);
        if (email is null) throw new UserImportException(400, "Enter a valid email address.");
        // Every syntactically valid address takes this same path. Account lookup happens only in the worker.
        var hash = AddressHash(email); var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"recovery:" + hash}, 0))", ct);
        if (!await db.EmailOutboxMessages.AnyAsync(e => e.Kind == "account_recovery" && e.RecipientHash == hash && e.CreatedAt > now.AddMinutes(-10), ct))
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('recovery-queue-budget'))", ct);
            if (await db.EmailOutboxMessages.CountAsync(e => e.Status == "queued" || e.Status == "sending", ct) >= 10000)
                throw new UserImportException(503, "Recovery is busy. Please try again later.");
            Add("account_recovery", new([email], "", ""), now.AddMinutes(15));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public void AddPasswordNotice(string email)
    {
        if (delivery.Value.Enabled) Add("password_changed", new([email], "Your MyShoppingList password was changed",
            "Your MyShoppingList password was changed. Existing sessions and AI/extension connections have been disconnected. "
            + "If you did not make this change, request password recovery from the MyShoppingList website or contact support."), Now.AddDays(1));
    }

    public async Task<bool> DeliverOneAsync(CancellationToken ct)
    {
        if (!delivery.Value.Enabled) return false;
        EmailOutboxMessage? row;
        var now = Now;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            row = await db.EmailOutboxMessages.FromSqlInterpolated($"""
                SELECT * FROM "EmailOutboxMessages"
                WHERE ("Status"='queued' AND "NextAttemptAt" <= {now})
                   OR ("Status"='sending' AND "LeaseExpiresAt" <= {now})
                ORDER BY "CreatedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).SingleOrDefaultAsync(ct);
            if (row is null) return false;
            row.Status = "sending"; row.Attempts++; row.ClaimToken = Guid.NewGuid(); row.LeaseExpiresAt = now.AddMinutes(2);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        if (row.ExpiresAt <= Now || row.Attempts > delivery.Value.MaximumAttempts)
        { await FinishAsync(row, row.ExpiresAt <= Now ? "expired" : "failed", ct); return true; }
        try
        {
            var message = JsonSerializer.Deserialize<OutboundEmail>(protector.Unprotect(row.ProtectedPayload!))
                ?? throw new CryptographicException();
            if (row.Kind == "account_recovery")
            {
                if (!recovery.Value.Enabled) { await FinishAsync(row, "cancelled", ct); return true; }
                var email = message.Recipients.Single();
                var user = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email && u.IsActive && !u.IsTrial, ct);
                if (user is null || await db.AccountCredentialChanges.AnyAsync(c => c.UserAccountId == user.Id && c.ChangedAt > row.CreatedAt, ct))
                { await FinishAsync(row, "suppressed", ct); return true; }
                var token = tokens.Create(user, row.ExpiresAt);
                var link = new Uri(new Uri(recovery.Value.WebsiteOrigin), "/account/recovery").AbsoluteUri + "#token=" + Uri.EscapeDataString(token);
                message = new([email], "Reset your MyShoppingList password",
                    "Use this link to choose a new MyShoppingList password:\n\n" + link
                    + "\n\nThis link expires within 15 minutes and can be used once. If you did not request it, ignore this message. Your password has not changed.");
            }
            // Claims are committed before SMTP; no customer/account lock is held over the network.
            if (!await db.EmailOutboxMessages.AnyAsync(e => e.Id == row.Id && e.Status == "sending" && e.ClaimToken == row.ClaimToken, ct)) return true;
            await transport.SendAsync(row.Id, message, ct);
            await FinishAsync(row, "sent", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            var terminal = row.Attempts >= delivery.Value.MaximumAttempts || row.ExpiresAt <= Now;
            await FinishAsync(row, terminal ? "failed" : "queued", ct);
            // Never log transport exceptions: they can contain recipients, credentials or reset links.
            if (terminal) logger.LogError("Email delivery exhausted its retry budget for message {MessageId}", row.Id);
        }
        return true;
    }

    private async Task FinishAsync(EmailOutboxMessage row, string status, CancellationToken ct)
    {
        var now = Now; var retry = status == "queued";
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.EmailOutboxMessages.Where(e => e.Id == row.Id && e.Status == "sending" && e.ClaimToken == row.ClaimToken)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, status).SetProperty(e => e.ClaimToken, (Guid?)null)
                .SetProperty(e => e.LeaseExpiresAt, (DateTime?)null).SetProperty(e => e.NextAttemptAt, now.AddMinutes(Math.Min(row.Attempts, 5)))
                .SetProperty(e => e.CompletedAt, retry ? null : (DateTime?)now)
                .SetProperty(e => e.ProtectedPayload, retry ? row.ProtectedPayload : null), ct);
        if (changed > 0 && row.Kind == "security_alert")
            await db.AlertDeliveries.Where(d => d.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Attempts, row.Attempts)
                .SetProperty(d => d.Status, retry ? "pending" : status).SetProperty(d => d.DeliveredAt, status == "sent" ? (DateTime?)now : null), ct);
        await tx.CommitAsync(ct);
    }

    public Task<int> RetainAsync(CancellationToken ct) => db.EmailOutboxMessages.Where(e => e.CompletedAt != null
        && e.CompletedAt < Now.AddDays(-7)).ExecuteDeleteAsync(ct);
}
