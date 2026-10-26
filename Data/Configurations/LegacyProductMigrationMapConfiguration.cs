using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class LegacyProductMigrationMapConfiguration : IEntityTypeConfiguration<LegacyProductMigrationMap>
{
    public void Configure(EntityTypeBuilder<LegacyProductMigrationMap> b)
    {
        b.HasKey(x => new { x.UserAccountId, x.LegacyProductId });
        b.HasIndex(x => x.PrivateProductId).IsUnique();
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.LegacyProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.PrivateProductId).OnDelete(DeleteBehavior.Restrict);
    }
}
