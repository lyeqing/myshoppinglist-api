using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class ShopProductConfiguration : IEntityTypeConfiguration<ShopProduct>
{
    public void Configure(EntityTypeBuilder<ShopProduct> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("ShopProducts", table => table.HasCheckConstraint("CK_ShopProducts_MatchConfidence", "\"MatchConfidence\" BETWEEN 0 AND 100"));
        builder.Property(x => x.ShopProductCode).HasMaxLength(200);
        builder.Property(x => x.ShopSku).HasMaxLength(200);
        builder.Property(x => x.NameAtShop).HasMaxLength(500).IsRequired();
        builder.Property(x => x.DescriptionAtShop).HasMaxLength(10000);
        builder.Property(x => x.ProductUrl).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.ImageUrl).HasMaxLength(2048);
        builder.Property(x => x.GTIN).HasMaxLength(14);
        builder.Property(x => x.MatchType).HasConversion<string>().HasMaxLength(30);
        builder.HasIndex(x => new { x.ProductId, x.ShopId });
        builder.HasIndex(x => new { x.ShopId, x.ShopProductCode }).IsUnique().HasFilter("\"ShopProductCode\" IS NOT NULL");
        builder.HasAlternateKey(x => new { x.Id, x.ShopId });
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Shop).WithMany().HasForeignKey(x => x.ShopId).OnDelete(DeleteBehavior.Restrict);
    }
}

