using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class LegacyShopProductMigrationMapConfiguration : IEntityTypeConfiguration<LegacyShopProductMigrationMap>
{
    public void Configure(EntityTypeBuilder<LegacyShopProductMigrationMap> b)
    {
        b.HasKey(x => new { x.UserAccountId, x.LegacyShopProductId });
        b.HasIndex(x => x.PrivateShopProductId).IsUnique();
        b.HasOne<LegacyProductMigrationMap>().WithMany().HasForeignKey(x => new { x.UserAccountId, x.LegacyProductId })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ShopProduct>().WithMany().HasForeignKey(x => x.LegacyShopProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ShopProduct>().WithMany().HasForeignKey(x => x.PrivateShopProductId).OnDelete(DeleteBehavior.Restrict);
    }
}
