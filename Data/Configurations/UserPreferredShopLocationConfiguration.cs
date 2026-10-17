using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class UserPreferredShopLocationConfiguration : IEntityTypeConfiguration<UserPreferredShopLocation>
{
    public void Configure(EntityTypeBuilder<UserPreferredShopLocation> builder)
    {
        builder.HasKey(p => new { p.UserAccountId, p.ShopLocationId });
        builder.HasOne(p => p.UserAccount).WithMany().HasForeignKey(p => p.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(p => p.ShopLocation).WithMany().HasForeignKey(p => p.ShopLocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.ShopLocationId);
        builder.ToTable("UserPreferredShopLocations", t => t.HasCheckConstraint("CK_UserPreferredShopLocations_Order", @"""DisplayOrder"" >= 0"));
    }
}
