using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class PersonalProductOwnershipMigrationTests
{
    private const string PreviousMigration = "20261022024735_AddShoppingListItemSubstitutions";

    [PostgreSqlFact]
    public async Task Upgrade_preserves_legacy_products_prices_lists_and_saved_preferences()
    {
        await using var fixture = await MigrationDatabase.CreateAsync();
        var db = fixture.Db;
        var account = Account();
        var mapping = Mapping(null, "legacy");
        var price = Price(mapping);
        var history = History(mapping);
        var list = new ShoppingList { UserAccount = account, Name = "Keep this list", CreatedDate = Now, UpdatedDate = Now };
        var item = new ShoppingListProduct
        {
            ShoppingList = list, Product = mapping.Product, Quantity = 3, Notes = "Keep these notes",
            IsPurchased = true, PurchasedDate = Now, AddedDate = Now, UpdatedDate = Now
        };
        db.AddRange(price, history, item);
        await db.SaveChangesAsync();
        db.UserProductPreferences.Add(new() { UserAccountId = account.Id, ProductId = mapping.ProductId, IsFavorite = true, IsEssential = true, UpdatedDate = Now });
        db.UserProductActivities.Add(new() { UserAccountId = account.Id, ProductId = mapping.ProductId, ShoppingListProductId = item.Id, Quantity = 3, AddedDate = Now, PurchasedDate = Now });
        await db.SaveChangesAsync();

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.Null((await db.Products.SingleAsync()).UserAccountId);
        Assert.Null((await db.ShopProducts.SingleAsync()).UserAccountId);
        var savedPrice = await db.ShopProductPrices.SingleAsync();
        Assert.Null(savedPrice.UserAccountId);
        Assert.Equal(4.5678m, savedPrice.Price);
        Assert.Equal(Now, savedPrice.CheckedDate);
        Assert.Null((await db.ShopProductPriceHistory.SingleAsync()).UserAccountId);
        Assert.Equal(Now, (await db.ShopProductPriceHistory.SingleAsync()).CheckedDate);
        var savedItem = await db.ShoppingListProducts.SingleAsync();
        Assert.Equal(item.Id, savedItem.Id);
        Assert.Equal(mapping.ProductId, savedItem.ProductId);
        Assert.Equal(3, savedItem.Quantity);
        Assert.Equal("Keep these notes", savedItem.Notes);
        Assert.True(savedItem.IsPurchased);
        Assert.Equal("Keep this list", (await db.ShoppingLists.SingleAsync()).Name);
        var preference = await db.UserProductPreferences.SingleAsync();
        Assert.True(preference.IsFavorite && preference.IsEssential);
        Assert.Equal(item.Id, (await db.UserProductActivities.SingleAsync()).ShoppingListProductId);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgreSqlFact]
    public async Task Same_retailer_product_and_price_are_independent_for_each_account()
    {
        await using var fixture = await MigrationDatabase.CreateAsync();
        var db = fixture.Db;
        var first = Account();
        var second = Account();
        db.AddRange(first, second);
        await db.SaveChangesAsync();
        var a = Mapping(first.Id, "same-code");
        var b = Mapping(second.Id, "same-code");
        var legacy = Mapping(null, "same-code");
        db.AddRange(Price(a), Price(b), Price(legacy), History(a), History(b));
        await db.SaveChangesAsync();
        await db.ShopProductPrices.Where(p => p.UserAccountId == first.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 9m));
        db.ChangeTracker.Clear();
        Assert.Equal(9m, (await db.ShopProductPrices.SingleAsync(p => p.UserAccountId == first.Id)).Price);
        Assert.Equal(4.5678m, (await db.ShopProductPrices.SingleAsync(p => p.UserAccountId == second.Id)).Price);
        Assert.Equal(4.5678m, (await db.ShopProductPrices.SingleAsync(p => p.UserAccountId == null)).Price);
        Assert.NotEqual(a.ProductId, b.ProductId);

        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Add(Mapping(first.Id, "same-code"));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [PostgreSqlFact]
    public async Task Database_rejects_cross_account_and_legacy_private_parent_links()
    {
        await using var fixture = await MigrationDatabase.CreateAsync();
        var db = fixture.Db;
        var first = Account();
        var second = Account();
        db.AddRange(first, second);
        await db.SaveChangesAsync();
        var a = Mapping(first.Id, "owned-a");
        var b = Mapping(second.Id, "owned-b");
        var legacy = Mapping(null, "legacy");
        db.AddRange(Price(a), Price(b), Price(legacy), History(a), History(b), History(legacy));
        await db.SaveChangesAsync();

        // Raw SQL avoids EF relationship fixup repairing deliberately invalid links.
        await Reject(db, $"""UPDATE "ShopProducts" SET "ProductId" = {b.ProductId} WHERE "Id" = {a.Id}""", "CK_PersonalProduct_ParentOwner");
        await Reject(db, $"""UPDATE "ShopProducts" SET "ProductId" = {legacy.ProductId} WHERE "Id" = {a.Id}""", "CK_PersonalProduct_ParentOwner");
        await Reject(db, $"""UPDATE "ShopProducts" SET "ProductId" = {a.ProductId} WHERE "Id" = {legacy.Id}""", "CK_PersonalProduct_ParentOwner");
        foreach (var table in new[] { "ShopProductPrices", "ShopProductPriceHistory" })
        {
            await RejectRaw(db, $"UPDATE \"{table}\" SET \"ShopProductId\" = {b.Id} WHERE \"ShopProductId\" = {a.Id}", "CK_PersonalProduct_ParentOwner");
            await RejectRaw(db, $"UPDATE \"{table}\" SET \"ShopProductId\" = {legacy.Id} WHERE \"ShopProductId\" = {a.Id}", "CK_PersonalProduct_ParentOwner");
            await RejectRaw(db, $"UPDATE \"{table}\" SET \"ShopProductId\" = {a.Id} WHERE \"ShopProductId\" = {legacy.Id}", "CK_PersonalProduct_ParentOwner");
        }
        foreach (var table in new[] { "Products", "ShopProducts", "ShopProductPrices", "ShopProductPriceHistory" })
        {
            await RejectRaw(db, $"UPDATE \"{table}\" SET \"UserAccountId\" = {second.Id} WHERE \"UserAccountId\" = {first.Id}", "CK_PersonalProduct_ImmutableOwner");
            await RejectRaw(db, $"UPDATE \"{table}\" SET \"UserAccountId\" = {first.Id} WHERE \"UserAccountId\" IS NULL", "CK_PersonalProduct_ImmutableOwner");
        }
    }

    [PostgreSqlFact]
    public async Task Database_rejects_missing_owner_and_duplicate_personal_url()
    {
        await using var fixture = await MigrationDatabase.CreateAsync();
        var db = fixture.Db;
        var account = Account();
        db.Add(account);
        await db.SaveChangesAsync();
        var mapping = Mapping(account.Id, "unique-url");
        db.Add(mapping);
        await db.SaveChangesAsync();
        var duplicate = Mapping(account.Id, "other-code");
        duplicate.ProductUrl = mapping.ProductUrl;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Add(duplicate);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Add(new Product { UserAccountId = long.MaxValue, Name = "Invalid owner", CreatedDate = Now, UpdatedDate = Now });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }

    [PostgreSqlFact]
    public async Task Rollback_refuses_to_discard_private_ownership()
    {
        await using var fixture = await MigrationDatabase.CreateAsync();
        var db = fixture.Db;
        var account = Account();
        db.Add(account);
        await db.SaveChangesAsync();
        db.Add(Mapping(account.Id, "private"));
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Contains("Cannot remove ownership", error.MessageText);
        Assert.Equal(account.Id, (await db.Products.SingleAsync()).UserAccountId);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static async Task Reject(MyShoppingListDbContext db, FormattableString sql, string constraint)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    private static async Task RejectRaw(MyShoppingListDbContext db, string sql, string constraint)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    private static DateTime Now => new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private static UserAccount Account() => new()
    {
        DisplayName = "Ownership test", IsTrial = true, IsActive = true,
        ExpiresDate = Now.AddYears(1), CreatedDate = Now, UpdatedDate = Now
    };
    private static ShopProduct Mapping(long? owner, string code) => new()
    {
        UserAccountId = owner,
        Product = new Product { UserAccountId = owner, Name = "Personal milk", GTIN = "9300000000000", CreatedDate = Now, UpdatedDate = Now },
        ShopId = 1, ShopProductCode = code, NameAtShop = "Personal milk",
        ProductUrl = "https://www.coles.com.au/product/" + code,
        FirstFoundDate = Now, LastFoundDate = Now
    };
    private static ShopProductPrice Price(ShopProduct mapping) => new()
    {
        UserAccountId = mapping.UserAccountId, ShopProduct = mapping, Price = 4.5678m,
        SourceUrl = mapping.ProductUrl, CheckedDate = Now, CreatedDate = Now, UpdatedDate = Now
    };
    private static ShopProductPriceHistory History(ShopProduct mapping) => new()
    {
        UserAccountId = mapping.UserAccountId, ShopProduct = mapping, Price = 4.5678m,
        SourceUrl = mapping.ProductUrl, CheckedDate = Now, CreatedDate = Now
    };

    private sealed class MigrationDatabase : IAsyncDisposable
    {
        private readonly string original;
        private readonly string schema;
        public MyShoppingListDbContext Db { get; }
        private MigrationDatabase(string connection, string name)
        {
            original = connection;
            schema = name;
            var isolated = new NpgsqlConnectionStringBuilder(connection) { SearchPath = name, Pooling = false }.ConnectionString;
            Db = new(new DbContextOptionsBuilder<MyShoppingListDbContext>().UseNpgsql(isolated).Options);
        }
        public static async Task<MigrationDatabase> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!;
            var fixture = new MigrationDatabase(connection, "codex_ownership_" + Guid.NewGuid().ToString("N"));
            await fixture.SchemaCommand($"CREATE SCHEMA \"{fixture.schema}\"");
            try { await fixture.Db.Database.MigrateAsync(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }
        private async Task SchemaCommand(string sql)
        {
            await using var connection = new NpgsqlConnection(original);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await SchemaCommand($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }
}
