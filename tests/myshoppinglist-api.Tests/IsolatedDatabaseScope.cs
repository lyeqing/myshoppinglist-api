using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;
using Npgsql;

namespace myshoppinglist_api.Tests;

// Only use inside the non-parallel "Import worker database" collection:
// existing fixtures obtain their connection string from the process environment.
internal sealed class IsolatedDatabaseScope : IAsyncDisposable
{
    private const string Variable = "MYSHOPPINGLIST_TEST_CONNECTION";
    private readonly string originalConnection;
    private readonly string schema = "codex_test_" + Guid.NewGuid().ToString("N");

    private IsolatedDatabaseScope(string connection) => originalConnection = connection;

    public static async Task<IsolatedDatabaseScope> CreateAsync()
    {
        var original = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(original))
            throw new InvalidOperationException("A PostgreSQL test connection is required.");
        var scope = new IsolatedDatabaseScope(original);
        await scope.SchemaCommandAsync($"CREATE SCHEMA \"{scope.schema}\"");
        try
        {
            // Never include public in the search path: missing test tables must fail,
            // rather than silently reading or changing application data.
            var isolated = new NpgsqlConnectionStringBuilder(original)
            {
                SearchPath = scope.schema,
                Pooling = false
            }.ConnectionString;
            await using var db = new MyShoppingListDbContext(new DbContextOptionsBuilder<MyShoppingListDbContext>()
                .UseNpgsql(isolated).Options);
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
            Environment.SetEnvironmentVariable(Variable, isolated);
            return scope;
        }
        catch
        {
            await scope.SchemaCommandAsync($"DROP SCHEMA \"{scope.schema}\" CASCADE");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(Variable, originalConnection);
        await SchemaCommandAsync($"DROP SCHEMA \"{schema}\" CASCADE");
    }

    private async Task SchemaCommandAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(originalConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
