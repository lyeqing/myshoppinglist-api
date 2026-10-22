using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class BrandDiscoveryJobConfiguration : IEntityTypeConfiguration<BrandDiscoveryJob>
{
    public void Configure(EntityTypeBuilder<BrandDiscoveryJob> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.Property(x => x.Status).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ErrorCode).HasMaxLength(100);
        builder.HasIndex(x => new { x.UserAccountId, x.ShoppingListProductId, x.ShopId }).IsUnique();
        builder.HasIndex(x => new { x.Status, x.NextAttemptDate });
        builder.HasOne(x => x.UserAccount).WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.ShoppingList).WithMany().HasForeignKey(x => new { x.ShoppingListId, x.UserAccountId })
            .HasPrincipalKey(x => new { x.Id, x.UserAccountId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.ShoppingListProduct).WithMany().HasForeignKey(x => new { x.ShoppingListProductId, x.ShoppingListId, x.ProductId })
            .HasPrincipalKey(x => new { x.Id, x.ShoppingListId, x.ProductId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Shop).WithMany().HasForeignKey(x => x.ShopId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable("BrandDiscoveryJobs", table =>
        {
            table.HasCheckConstraint("CK_BrandDiscoveryJobs_Counts", "\"Attempts\" >= 0 AND \"FoundCount\" >= 0");
            table.HasCheckConstraint("CK_BrandDiscoveryJobs_Status", "\"Status\" IN ('Queued','Processing','WaitingForExtension','Completed','Failed','Cancelled')");
            table.HasCheckConstraint("CK_BrandDiscoveryJobs_Claim", "(\"Status\" = 'Processing' AND \"ClaimToken\" IS NOT NULL AND \"LeaseExpiresDate\" IS NOT NULL) OR (\"Status\" <> 'Processing' AND \"ClaimToken\" IS NULL AND \"LeaseExpiresDate\" IS NULL)");
        });
    }
}
