using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;
namespace myshoppinglist_api.Data.Configurations;
public sealed class ShoppingListItemSubstitutionConfiguration : IEntityTypeConfiguration<ShoppingListItemSubstitution>
{
    public void Configure(EntityTypeBuilder<ShoppingListItemSubstitution> b)
    {
        b.HasKey(s => s.Id);
        b.HasIndex(s => new { s.ShoppingListProductId, s.ShopId }).IsUnique();
        b.Property(s => s.OriginalFingerprint).HasMaxLength(64).IsRequired();
        b.Property(s => s.AlternativeFingerprint).HasMaxLength(64).IsRequired();
        b.HasOne(s => s.ShoppingList).WithMany().HasForeignKey(s => new { s.ShoppingListId, s.UserAccountId })
            .HasPrincipalKey(l => new { l.Id, l.UserAccountId }).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(s => s.ShoppingListProduct).WithMany().HasForeignKey(s => new { s.ShoppingListProductId, s.ShoppingListId, s.ProductId })
            .HasPrincipalKey(i => new { i.Id, i.ShoppingListId, i.ProductId }).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(s => s.Shop).WithMany().HasForeignKey(s => s.ShopId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(s => s.ShopProduct).WithMany().HasForeignKey(s => s.ShopProductId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("ShoppingListItemSubstitutions", t => t.HasCheckConstraint("CK_ShoppingListItemSubstitutions_Quantity", "\"Quantity\" BETWEEN 1 AND 999"));
    }
}
