using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Mcp;

public sealed class McpMutationCoordinator(MyShoppingListDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<McpMutationResult> ExecuteAsync(long accountId, Guid requestId, string operation, object input,
        Func<Task<McpMutationResult>> mutate, CancellationToken ct)
    {
        // OpenIddict validation shares this request scope and can track read-only
        // token entities. Preserve the list service's clean-context safeguard.
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("MCP commands require a context without pending changes.");
        db.ChangeTracker.Clear();
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { operation, input }, Json)));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Serialize MCP retries without taking an account row lock ahead of the
            // existing services' import-advisory/account/list lock order.
            var key = "mcp-mutations:" + accountId.ToString(CultureInfo.InvariantCulture);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.IsActive, ct);
            if (account is null) throw new McpException("account_unavailable");
            if (account.IsTrial) throw new McpException("account_not_supported");
            var previous = await db.McpMutationReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.UserAccountId == accountId && r.RequestId == requestId, ct);
            if (previous is not null)
            {
                if (previous.Operation != operation || previous.RequestHash != hash) throw new McpException("request_id_conflict");
                return (JsonSerializer.Deserialize<McpMutationResult>(previous.ResultJson, Json)
                    ?? throw new InvalidOperationException("Invalid mutation receipt.")) with { Replayed = true };
            }
            var result = await mutate();
            db.McpMutationReceipts.Add(new() { UserAccountId = accountId, RequestId = requestId, Operation = operation,
                RequestHash = hash, ResultJson = JsonSerializer.Serialize(result, Json), CreatedDate = clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        finally { db.ChangeTracker.Clear(); }
    }
}
