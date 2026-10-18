using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class UserProductActivityConfiguration : IEntityTypeConfiguration<UserProductActivity>
{
    public void Configure(EntityTypeBuilder<UserProductActivity> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).UseIdentityByDefaultColumn();
        builder.HasOne(p => p.UserAccount).WithMany().HasForeignKey(p => p.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(p => p.Product).WithMany().HasForeignKey(p => p.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.ShoppingListProduct).WithMany().HasForeignKey(p => p.ShoppingListProductId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(p => p.ShoppingListProductId).IsUnique();
        builder.HasIndex(p => new { p.UserAccountId, p.ProductId });
        builder.HasIndex(p => p.ProductId);
        builder.ToTable("UserProductActivities", t => t.HasCheckConstraint("CK_UserProductActivities_Quantity", "\"Quantity\" > 0"));
    }
}
