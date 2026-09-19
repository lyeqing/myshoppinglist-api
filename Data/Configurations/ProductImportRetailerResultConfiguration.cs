using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class ProductImportRetailerResultConfiguration : IEntityTypeConfiguration<ProductImportRetailerResult>
{
    public void Configure(EntityTypeBuilder<ProductImportRetailerResult> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("ProductImportRetailerResults", table => table.HasCheckConstraint("CK_ProductImportRetailerResults_MatchConfidence", "\"MatchConfidence\" BETWEEN 0 AND 100"));
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.MatchType).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.ErrorCode).HasMaxLength(100);
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);
        builder.HasIndex(x => new { x.ProductImportJobId, x.ShopId }).IsUnique();
        builder.HasOne(x => x.ProductImportJob).WithMany().HasForeignKey(x => x.ProductImportJobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Shop).WithMany().HasForeignKey(x => x.ShopId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ShopProduct).WithMany().HasForeignKey(x => new { x.ShopProductId, x.ShopId })
            .HasPrincipalKey(x => new { x.Id, x.ShopId }).OnDelete(DeleteBehavior.Restrict);
    }
}

