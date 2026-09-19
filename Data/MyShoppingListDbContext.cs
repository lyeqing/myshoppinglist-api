using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data;

public class MyShoppingListDbContext(DbContextOptions<MyShoppingListDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();
    public DbSet<ShoppingListProduct> ShoppingListProducts => Set<ShoppingListProduct>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<ShopLocation> ShopLocations => Set<ShopLocation>();
    public DbSet<ShopProduct> ShopProducts => Set<ShopProduct>();
    public DbSet<ShopProductPrice> ShopProductPrices => Set<ShopProductPrice>();
    public DbSet<ShopProductPriceHistory> ShopProductPriceHistory => Set<ShopProductPriceHistory>();
    public DbSet<ProductImportJob> ProductImportJobs => Set<ProductImportJob>();
    public DbSet<ProductImportRetailerResult> ProductImportRetailerResults => Set<ProductImportRetailerResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyShoppingListDbContext).Assembly);
    }
}

