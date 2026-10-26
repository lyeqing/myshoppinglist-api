using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using myshoppinglist_api.Models;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class LegacyProductMigrationSchemaTests
{
    [PostgreSqlFact]
    public async Task Upgrade_and_empty_rollback_preserve_data_and_existing_owner_guards()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(latest: false); await f.SeedAsync();
        var before = await f.FingerprintAsync(legacyOnly: true);
        await f.Db.GetService<IMigrator>().MigrateAsync();
        Assert.True((await f.Service.PreviewAsync()).SchemaReady);
        Assert.Equal(0, await f.Db.Products.CountAsync(p => p.UserAccountId != null));
        Assert.Empty(await f.Db.LegacyProductMigrationMaps.ToArrayAsync());
        Assert.False(f.Db.Database.HasPendingModelChanges());
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        await f.Db.GetService<IMigrator>().MigrateAsync(MigrationRehearsalScope.PreviousMigration);
        Assert.False((await f.Service.PreviewAsync()).SchemaReady);
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        var own = new Product { UserAccountId = f.A.Id, Name = "Private A", CreatedDate = f.Past, UpdatedDate = f.Past };
        f.Db.Products.Add(own); await f.Db.SaveChangesAsync();
        f.Db.ShopProducts.Add(new() { UserAccountId = f.B.Id, ProductId = own.Id, ShopId = 1,
            NameAtShop = "Invalid foreign mapping", ProductUrl = "https://www.coles.com.au/product/999999", FirstFoundDate = f.Past, LastFoundDate = f.Past });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
        f.Db.ChangeTracker.Clear();
    }

    [PostgreSqlFact]
    public async Task Product_receipts_enforce_ownership_uniqueness_and_immutability()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var own = new Product { UserAccountId = f.A.Id, Name = "Copy A", CreatedDate = f.Past, UpdatedDate = f.Past };
        var foreign = new Product { UserAccountId = f.B.Id, Name = "Copy B", CreatedDate = f.Past, UpdatedDate = f.Past };
        var secondOwn = new Product { UserAccountId = f.A.Id, Name = "Second A", CreatedDate = f.Past, UpdatedDate = f.Past };
        f.Db.AddRange(own, foreign, secondOwn); await f.Db.SaveChangesAsync();
        f.Db.LegacyProductMigrationMaps.Add(new() { UserAccountId = f.A.Id, LegacyProductId = f.Source.Id, PrivateProductId = foreign.Id, CreatedAt = f.Past });
        var wrong = await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(wrong.InnerException).SqlState); f.Db.ChangeTracker.Clear();
        f.Db.LegacyProductMigrationMaps.Add(new() { UserAccountId = f.A.Id, LegacyProductId = f.Source.Id, PrivateProductId = own.Id, CreatedAt = f.Past });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        f.Db.LegacyProductMigrationMaps.Add(new() { UserAccountId = f.A.Id, LegacyProductId = f.Source.Id, PrivateProductId = secondOwn.Id, CreatedAt = f.Past });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(duplicate.InnerException).SqlState); f.Db.ChangeTracker.Clear();
        var changed = await Assert.ThrowsAsync<PostgresException>(() => f.Db.LegacyProductMigrationMaps
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.PrivateProductId, secondOwn.Id)));
        Assert.Equal("23514", changed.SqlState);
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => f.Db.LegacyProductMigrationMaps.ExecuteDeleteAsync());
        Assert.Equal("23514", deleted.SqlState);
        Assert.Equal(own.Id, (await f.Db.LegacyProductMigrationMaps.SingleAsync()).PrivateProductId);
    }

    [PostgreSqlFact]
    public async Task Retailer_receipts_reject_cross_owner_links_deletion_and_source_or_copy_reparenting()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        await f.Service.RehearseAsync((await f.Service.PreviewAsync()).PlanHash);
        var mapping = await f.Db.LegacyShopProductMigrationMaps.AsNoTracking().SingleAsync(m => m.UserAccountId == f.A.Id && m.LegacyShopProductId == f.SourceMap.Id);
        var alternative = await f.Db.LegacyProductMigrationMaps.AsNoTracking().SingleAsync(m => m.UserAccountId == f.A.Id && m.LegacyProductId == f.Alternative.Id);
        var movedCopy = await Assert.ThrowsAsync<PostgresException>(() => f.Db.ShopProducts.Where(m => m.Id == mapping.PrivateShopProductId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.ProductId, alternative.PrivateProductId)));
        Assert.Equal("23514", movedCopy.SqlState);
        var movedSource = await Assert.ThrowsAsync<PostgresException>(() => f.Db.ShopProducts.Where(m => m.Id == f.SourceMap.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.ProductId, f.Alternative.Id)));
        Assert.Equal("23514", movedSource.SqlState);
        f.Db.LegacyShopProductMigrationMaps.Add(new() { UserAccountId = f.B.Id, LegacyProductId = f.Source.Id,
            LegacyShopProductId = f.SourceMap.Id, PrivateShopProductId = mapping.PrivateShopProductId, CreatedAt = f.Past });
        var wrong = await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(wrong.InnerException).SqlState); f.Db.ChangeTracker.Clear();
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => f.Db.LegacyShopProductMigrationMaps.ExecuteDeleteAsync());
        Assert.Equal("23514", deleted.SqlState);
        Assert.Equal(5, await f.Db.LegacyShopProductMigrationMaps.CountAsync());
    }

    [PostgreSqlFact]
    public async Task Rollback_after_conversion_refuses_to_erase_receipts_or_private_data()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        await f.Service.RehearseAsync((await f.Service.PreviewAsync()).PlanHash);
        var before = await f.FingerprintAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync(MigrationRehearsalScope.PreviousMigration));
        Assert.Contains("Cannot remove migration receipts", error.MessageText);
        Assert.Contains(MigrationRehearsalScope.CopyMigration, await f.Db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Disabled_guards_are_reported_as_not_ready_and_block_rehearsal()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        await f.Db.Database.ExecuteSqlRawAsync("ALTER TABLE \"LegacyProductMigrationMaps\" DISABLE TRIGGER \"TR_LegacyProductMigrationMaps_Owners\"");
        var plan = await f.Service.PreviewAsync(); Assert.False(plan.SchemaReady);
        var before = await f.FingerprintAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RehearseAsync(plan.PlanHash));
        Assert.Equal(before, await f.FingerprintAsync());
    }
}
