using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data;

public class MyShoppingListDbContext(DbContextOptions<MyShoppingListDbContext> options) : DbContext(options)
{
    public DbSet<EmailOutboxMessage> EmailOutboxMessages => Set<EmailOutboxMessage>();
    public DbSet<AccountCredentialChange> AccountCredentialChanges => Set<AccountCredentialChange>();
    public DbSet<BillingCustomer> BillingCustomers => Set<BillingCustomer>();
    public DbSet<BillingWebhookEvent> BillingWebhookEvents => Set<BillingWebhookEvent>();
    public DbSet<BillingCheckoutAttempt> BillingCheckoutAttempts => Set<BillingCheckoutAttempt>();
    public DbSet<FeedbackTicket> FeedbackTickets => Set<FeedbackTicket>();
    public DbSet<OperationalEvent> OperationalEvents => Set<OperationalEvent>();
    public DbSet<DailyOperationalSummary> DailyOperationalSummaries => Set<DailyOperationalSummary>();
    public DbSet<OperationalIncident> OperationalIncidents => Set<OperationalIncident>();
    public DbSet<AlertDelivery> AlertDeliveries => Set<AlertDelivery>();
    public DbSet<OperationsAdminAudit> OperationsAdminAudits => Set<OperationsAdminAudit>();
    public DbSet<UserCurrentList> UserCurrentLists => Set<UserCurrentList>();
    public DbSet<AiConnection> AiConnections => Set<AiConnection>();
    public DbSet<McpMutationReceipt> McpMutationReceipts => Set<McpMutationReceipt>();
    public DbSet<UserCurrentListRevision> UserCurrentListRevisions => Set<UserCurrentListRevision>();
    public DbSet<ExtensionDevice> ExtensionDevices => Set<ExtensionDevice>();
    public DbSet<ExtensionTask> ExtensionTasks => Set<ExtensionTask>();
    public DbSet<LegacyProductMigrationMap> LegacyProductMigrationMaps => Set<LegacyProductMigrationMap>();
    public DbSet<LegacyShopProductMigrationMap> LegacyShopProductMigrationMaps => Set<LegacyShopProductMigrationMap>();
    public DbSet<ShoppingListItemSubstitution> ShoppingListItemSubstitutions => Set<ShoppingListItemSubstitution>();
    public DbSet<BrandDiscoveryJob> BrandDiscoveryJobs => Set<BrandDiscoveryJob>();
    public DbSet<ProductBrandSuggestion> ProductBrandSuggestions => Set<ProductBrandSuggestion>();
    public DbSet<UserProductPreference> UserProductPreferences => Set<UserProductPreference>();
    public DbSet<UserProductActivity> UserProductActivities => Set<UserProductActivity>();
    public DbSet<ProductAlternativeRelationship> ProductAlternativeRelationships => Set<ProductAlternativeRelationship>();
    public DbSet<RetailerWorkloadState> RetailerWorkloadStates => Set<RetailerWorkloadState>();
    public DbSet<ContributionObservation> ContributionObservations => Set<ContributionObservation>();
    public DbSet<AccountAdminAudit> AccountAdminAudits => Set<AccountAdminAudit>();
    public DbSet<UserExtensionImportTask> UserExtensionImportTasks => Set<UserExtensionImportTask>();
    public DbSet<ColesExtensionTask> ColesExtensionTasks => Set<ColesExtensionTask>();
    public DbSet<PackDiscoveryJob> PackDiscoveryJobs => Set<PackDiscoveryJob>();
    public DbSet<UserPreferredShopLocation> UserPreferredShopLocations => Set<UserPreferredShopLocation>();
    public DbSet<UserPreferredShop> UserPreferredShops => Set<UserPreferredShop>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();
    public DbSet<ShoppingListProduct> ShoppingListProducts => Set<ShoppingListProduct>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<ShopLocationReview> ShopLocationReviews => Set<ShopLocationReview>();
    public DbSet<ShopLocation> ShopLocations => Set<ShopLocation>();
    public DbSet<ShopProduct> ShopProducts => Set<ShopProduct>();
    public DbSet<ShopProductPrice> ShopProductPrices => Set<ShopProductPrice>();
    public DbSet<ShopProductPriceHistory> ShopProductPriceHistory => Set<ShopProductPriceHistory>();
    public DbSet<PersonalRetailerComparison> PersonalRetailerComparisons => Set<PersonalRetailerComparison>();
    public DbSet<ProductImportJob> ProductImportJobs => Set<ProductImportJob>();
    public DbSet<ProductImportRetailerResult> ProductImportRetailerResults => Set<ProductImportRetailerResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseOpenIddict();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyShoppingListDbContext).Assembly);
    }
}
