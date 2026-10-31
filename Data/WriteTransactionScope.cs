using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace myshoppinglist_api.Data;

// Services retain their own transaction by default. A command coordinator may supply
// an outer transaction so its mutation and idempotency receipt commit together.
public sealed class WriteTransactionScope : IAsyncDisposable
{
    private readonly IDbContextTransaction? owned;
    private WriteTransactionScope(IDbContextTransaction? transaction) => owned = transaction;
    public static async Task<WriteTransactionScope> BeginAsync(DbContext db, CancellationToken ct) =>
        new(db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null);
    public Task CommitAsync(CancellationToken ct) => owned?.CommitAsync(ct) ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}
