using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class ProductImportJobConfiguration : IEntityTypeConfiguration<ProductImportJob>
{
    public void Configure(EntityTypeBuilder<ProductImportJob> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("ProductImportJobs", table =>
        {
            table.HasCheckConstraint("CK_ProductImportJobs_Quantity", "\"RequestedQuantity\" > 0");
            table.HasCheckConstraint("CK_ProductImportJobs_AttemptCount", "\"AttemptCount\" >= 0");
            table.HasCheckConstraint("CK_ProductImportJobs_ListItem", "\"ShoppingListProductId\" IS NULL OR \"ProductId\" IS NOT NULL");
            table.HasCheckConstraint("CK_ProductImportJobs_Claim", """("Status" = 'Processing' AND "ClaimToken" IS NOT NULL AND "LeaseExpiresDate" IS NOT NULL) OR ("Status" <> 'Processing' AND "ClaimToken" IS NULL AND "LeaseExpiresDate" IS NULL)""");
        });
        builder.Property(x => x.SourceUrl).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.NormalisedSourceUrl).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsConcurrencyToken();
        builder.Property(x => x.ProgressStage).HasConversion<string>().HasMaxLength(40);
        builder.Property(x => x.ClaimToken).IsConcurrencyToken();
        builder.Property(x => x.ErrorCode).HasMaxLength(100);
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);
        builder.HasIndex(x => new { x.Status, x.NextAttemptDate, x.CreatedDate });
        builder.HasIndex(x => new { x.Status, x.LeaseExpiresDate });
        builder.HasIndex(x => new { x.ShoppingListId, x.Status });
        builder.HasOne(x => x.UserAccount).WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        // The composite key ensures the job owner is also the list owner.
        builder.HasOne(x => x.ShoppingList).WithMany().HasForeignKey(x => new { x.ShoppingListId, x.UserAccountId })
            .HasPrincipalKey(x => new { x.Id, x.UserAccountId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.SourceShop).WithMany().HasForeignKey(x => x.SourceShopId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ShoppingListProduct).WithMany().HasForeignKey(x => new { x.ShoppingListProductId, x.ShoppingListId, x.ProductId })
            .HasPrincipalKey(x => new { x.Id, x.ShoppingListId, x.ProductId }).OnDelete(DeleteBehavior.Cascade);
    }
}

