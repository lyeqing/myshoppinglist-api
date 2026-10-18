using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ProductLibraryMigrationTests
{
    [PostgreSqlFact]
    public async Task ProductLibrary_migration_backfills_existing_items_and_round_trips_without_changing_lists()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        var before = await f.ItemAsync();
        await f.Scope.Db.Database.ExecuteSqlRawAsync("DROP TABLE \"UserProductActivities\"; DROP TABLE \"UserProductPreferences\"");
        var migrations = f.Scope.Db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, m => m.EndsWith("_AddProductLibrary", StringComparison.Ordinal));
        var history = f.Scope.Db.GetService<IHistoryRepository>();
        await f.Scope.Db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
        var migrator = f.Scope.Db.GetService<IMigrator>();
        await f.Scope.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(migrations[index - 1], migrations[index]));
        var activity = Assert.Single(await f.Scope.Db.UserProductActivities.ToArrayAsync());
        Assert.Equal(before.Id, activity.ShoppingListProductId); Assert.Equal(before.Product.Id, activity.ProductId);
        Assert.Equal(before, await f.ItemAsync());
        await f.Scope.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(migrations[index], migrations[index - 1]));
        Assert.Equal(before, await f.ItemAsync());
        await f.Scope.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(migrations[index - 1], migrations[index]));
        Assert.Single(await f.Scope.Db.UserProductActivities.ToArrayAsync());
    }
}
