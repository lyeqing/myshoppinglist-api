using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class ShoppingListProductConfiguration : IEntityTypeConfiguration<ShoppingListProduct>
{
    public void Configure(EntityTypeBuilder<ShoppingListProduct> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("ShoppingListProducts", table => table.HasCheckConstraint("CK_ShoppingListProducts_Quantity", "\"Quantity\" > 0"));
        builder.Property(x => x.Notes).HasMaxLength(4000);
        builder.HasIndex(x => new { x.ShoppingListId, x.ProductId }).IsUnique();
        builder.HasAlternateKey(x => new { x.Id, x.ShoppingListId, x.ProductId });
        builder.HasOne(x => x.ShoppingList).WithMany().HasForeignKey(x => x.ShoppingListId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PreferredShop).WithMany().HasForeignKey(x => x.PreferredShopId).OnDelete(DeleteBehavior.Restrict);
    }
}

