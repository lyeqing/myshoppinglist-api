using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using Npgsql;

namespace myshoppinglist_api.Tests;

public class DatabaseModelTests
{
    private static MyShoppingListDbContext ModelContext() => new(new DbContextOptionsBuilder<MyShoppingListDbContext>()
        .UseNpgsql("Host=localhost;Database=model_only").Options);

    [Fact]
    public void Model_contains_only_the_twelve_application_entities()
    {
        using var db = ModelContext();
        Assert.Equal(12, db.Model.GetEntityTypes().Count());
        Assert.DoesNotContain(db.Model.GetEntityTypes(), e => e.Name.Contains("Company"));
        Assert.All(db.Model.GetEntityTypes().SelectMany(e => e.GetProperties()), p => Assert.False(p.IsShadowProperty()));
    }

    [Fact]
    public void Five_retailers_have_stable_unique_codes()
    {
        using var db = ModelContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var seeds = model.FindEntityType(typeof(Shop))!.GetSeedData().ToArray();
        Assert.Equal(new[] { "aldi", "coles", "foodland", "iga", "woolworths" },
            seeds.Select(x => (string)x["Code"]!).Order().ToArray());
        Assert.Equal(5, seeds.Select(x => x["Id"]).Distinct().Count());
    }

    [Fact]
    public void Prices_use_decimal_precision_and_nullable_location()
    {
        using var db = ModelContext();
        foreach (var type in new[] { typeof(ShopProductPrice), typeof(ShopProductPriceHistory) })
        {
            var entity = db.Model.FindEntityType(type)!;
            foreach (var name in new[] { "Price", "NormalPrice", "UnitPrice" })
            {
                Assert.Equal(18, entity.FindProperty(name)!.GetPrecision());
                Assert.Equal(4, entity.FindProperty(name)!.GetScale());
            }
            Assert.True(entity.FindProperty("ShopLocationId")!.IsNullable);
        }
    }

    [Fact]
    public void Catalogue_is_shared_and_list_entries_do_not_copy_product_metadata()
    {
        using var db = ModelContext();
        var item = db.Model.FindEntityType(typeof(ShoppingListProduct))!;
        foreach (var name in new[] { "Name", "Brand", "GTIN", "ImageUrl", "Description" })
            Assert.Null(item.FindProperty(name));
        var product = db.Model.FindEntityType(typeof(Product))!;
        Assert.Null(product.FindProperty("UserAccountId"));
        Assert.False(Assert.Single(product.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "GTIN" })).IsUnique);
        Assert.All(item.GetForeignKeys().Where(f => f.PrincipalEntityType.ClrType == typeof(Product)),
            f => Assert.Equal(DeleteBehavior.Restrict, f.DeleteBehavior));
    }

    [Fact]
    public void Claim_token_and_status_are_concurrency_guards()
    {
        using var db = ModelContext();
        var job = db.Model.FindEntityType(typeof(ProductImportJob))!;
        Assert.True(job.FindProperty("Status")!.IsConcurrencyToken);
        Assert.True(job.FindProperty("ClaimToken")!.IsConcurrencyToken);
        Assert.Contains(job.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(ShoppingList)
            && f.Properties.Select(p => p.Name).SequenceEqual(new[] { "ShoppingListId", "UserAccountId" }));
    }

    [PostgreSqlFact]
    public async Task Current_price_uniqueness_also_covers_null_locations()
    {
        await using var db = DatabaseContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var mapping = Mapping();
        db.Add(Price(mapping));
        await db.SaveChangesAsync();
        db.Add(Price(mapping));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [PostgreSqlFact]
    public async Task Separate_price_scopes_and_history_preserve_decimal_values()
    {
        await using var db = DatabaseContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var mapping = Mapping();
        var online = Price(mapping);
        var catalogue = Price(mapping);
        catalogue.PriceScope = PriceScope.Catalogue;
        db.AddRange(online, catalogue);
        db.Add(new ShopProductPriceHistory
        {
            ShopProduct = mapping, Price = 12.3456m, UnitPrice = 0.1234m,
            SourceUrl = mapping.ProductUrl, CheckedDate = DateTime.UtcNow, CreatedDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.ShopProductPrices.CountAsync(x => x.ShopProductId == mapping.Id));
        Assert.Equal(12.3456m, (await db.ShopProductPriceHistory.SingleAsync(x => x.ShopProductId == mapping.Id)).Price);
    }

    [PostgreSqlFact]
    public async Task Duplicate_product_in_same_list_is_rejected()
    {
        await using var db = DatabaseContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var list = List();
        var product = Mapping().Product;
        db.Add(new ShoppingListProduct { ShoppingList = list, Product = product });
        await db.SaveChangesAsync();
        db.Add(new ShoppingListProduct { ShoppingList = list, Product = product });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [PostgreSqlFact]
    public async Task Job_cannot_reference_another_users_list()
    {
        await using var db = DatabaseContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var list = List();
        var other = Trial();
        db.AddRange(list, other);
        await db.SaveChangesAsync();
        // Raw SQL exercises the database constraint without EF relationship fixup changing the owner.
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ProductImportJobs" ("UserAccountId", "ShoppingListId", "SourceUrl", "NormalisedSourceUrl", "RequestedQuantity", "Status", "ProgressStage", "AttemptCount", "CreatedDate")
            VALUES ({other.Id}, {list.Id}, 'https://www.coles.com.au/product/test', 'https://www.coles.com.au/product/test', 1, 'Queued', 'Queued', 0, {DateTime.UtcNow})
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [PostgreSqlFact]
    public async Task A_stale_claim_cannot_overwrite_a_claimed_job()
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = new DbContextOptionsBuilder<MyShoppingListDbContext>().UseNpgsql(connection).Options;
        await using var first = new MyShoppingListDbContext(options);
        await using var second = new MyShoppingListDbContext(options);
        await first.Database.UseTransactionAsync(transaction);
        await second.Database.UseTransactionAsync(transaction);
        var list = List();
        var job = new ProductImportJob
        {
            UserAccount = list.UserAccount, ShoppingList = list,
            SourceUrl = "https://www.coles.com.au/product/test",
            NormalisedSourceUrl = "https://www.coles.com.au/product/test", CreatedDate = DateTime.UtcNow
        };
        first.Add(job);
        await first.SaveChangesAsync();
        var stale = await second.ProductImportJobs.SingleAsync(x => x.Id == job.Id);
        job.Status = ProductImportJobStatus.Processing;
        job.ClaimToken = Guid.NewGuid();
        job.LeaseExpiresDate = DateTime.UtcNow.AddMinutes(10);
        job.AttemptCount++;
        await first.SaveChangesAsync();
        stale.Status = ProductImportJobStatus.Processing;
        stale.ClaimToken = Guid.NewGuid();
        stale.LeaseExpiresDate = DateTime.UtcNow.AddMinutes(10);
        stale.AttemptCount++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [PostgreSqlFact]
    public async Task Failed_retailer_result_survives_without_a_product_mapping()
    {
        await using var db = DatabaseContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var list = List();
        var job = new ProductImportJob
        {
            UserAccount = list.UserAccount, ShoppingList = list,
            SourceUrl = "https://www.coles.com.au/product/test",
            NormalisedSourceUrl = "https://www.coles.com.au/product/test", CreatedDate = DateTime.UtcNow
        };
        var result = new ProductImportRetailerResult
        {
            ProductImportJob = job, ShopId = 2, Status = RetailerLookupStatus.Unavailable,
            ErrorCode = "retailer_timeout", CreatedDate = DateTime.UtcNow, UpdatedDate = DateTime.UtcNow
        };
        db.Add(result);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var stored = await db.ProductImportRetailerResults.SingleAsync(x => x.Id == result.Id);
        Assert.Equal(RetailerLookupStatus.Unavailable, stored.Status);
        Assert.Null(stored.ShopProductId);
    }

    private static string ConnectionString() => Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!;
    private static MyShoppingListDbContext DatabaseContext() => new(new DbContextOptionsBuilder<MyShoppingListDbContext>()
        .UseNpgsql(ConnectionString()).Options);
    private static UserAccount Trial() => new()
    {
        IsTrial = true, DisplayName = "Schema verification", ExpiresDate = DateTime.UtcNow.AddHours(3),
        CreatedDate = DateTime.UtcNow, UpdatedDate = DateTime.UtcNow
    };
    private static ShoppingList List() => new() { UserAccount = Trial(), Name = "Schema verification" };
    private static ShopProduct Mapping() => new()
    {
        ShopId = 1, Product = new Product { Name = "Schema verification", CreatedDate = DateTime.UtcNow, UpdatedDate = DateTime.UtcNow },
        NameAtShop = "Schema verification", ProductUrl = "https://www.coles.com.au/product/test",
        ShopProductCode = Guid.NewGuid().ToString("N"), FirstFoundDate = DateTime.UtcNow, LastFoundDate = DateTime.UtcNow
    };
    private static ShopProductPrice Price(ShopProduct mapping) => new()
    {
        ShopProduct = mapping, Price = 12.3456m, PriceScope = PriceScope.Online,
        SourceUrl = mapping.ProductUrl, CheckedDate = DateTime.UtcNow, CreatedDate = DateTime.UtcNow, UpdatedDate = DateTime.UtcNow
    };
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")))
            Skip = "Set MYSHOPPINGLIST_TEST_CONNECTION to a migrated PostgreSQL database. Tests roll back their data.";
    }
}
