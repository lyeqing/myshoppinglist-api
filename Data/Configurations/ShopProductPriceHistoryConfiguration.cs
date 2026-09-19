using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class ShopProductPriceHistoryConfiguration : IEntityTypeConfiguration<ShopProductPriceHistory>
{
    public void Configure(EntityTypeBuilder<ShopProductPriceHistory> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("ShopProductPriceHistory", table =>
        {
            table.HasCheckConstraint("CK_ShopProductPriceHistory_Price", "\"Price\" >= 0 AND (\"NormalPrice\" IS NULL OR \"NormalPrice\" >= 0) AND (\"UnitPrice\" IS NULL OR \"UnitPrice\" >= 0)");
            table.HasCheckConstraint("CK_ShopProductPriceHistory_SpecialDates", "\"SpecialStartDate\" IS NULL OR \"SpecialEndDate\" IS NULL OR \"SpecialEndDate\" >= \"SpecialStartDate\"");
            table.HasCheckConstraint("CK_ShopProductPriceHistory_StoreScope", "\"PriceScope\" <> 'StoreSpecific' OR \"ShopLocationId\" IS NOT NULL");
        });
        builder.Property(x => x.Price).HasPrecision(18, 4);
        builder.Property(x => x.NormalPrice).HasPrecision(18, 4);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 4);
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.SpecialType).HasMaxLength(100);
        builder.Property(x => x.SpecialDescription).HasMaxLength(2000);
        builder.Property(x => x.PriceScope).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.SourceUrl).HasMaxLength(2048).IsRequired();
        builder.HasOne(x => x.ShopProduct).WithMany().HasForeignKey(x => x.ShopProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ShopLocation).WithMany().HasForeignKey(x => x.ShopLocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ShopProductId, x.CheckedDate });
    }
}

