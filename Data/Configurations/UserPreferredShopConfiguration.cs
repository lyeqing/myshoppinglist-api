using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class UserPreferredShopConfiguration : IEntityTypeConfiguration<UserPreferredShop>
{
    public void Configure(EntityTypeBuilder<UserPreferredShop> builder)
    {
        builder.HasKey(p => new { p.UserAccountId, p.ShopId });
        builder.HasOne(p => p.UserAccount).WithMany().HasForeignKey(p => p.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(p => p.Shop).WithMany().HasForeignKey(p => p.ShopId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.ShopId);
        builder.ToTable("UserPreferredShops", t => t.HasCheckConstraint("CK_UserPreferredShops_Order", @"""DisplayOrder"" >= 0"));
    }
}
