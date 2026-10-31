using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class UserCurrentListConfiguration : IEntityTypeConfiguration<UserCurrentList>
{
    public void Configure(EntityTypeBuilder<UserCurrentList> builder)
    {
        builder.HasKey(c => c.UserAccountId);
        builder.HasOne(c => c.UserAccount).WithMany().HasForeignKey(c => c.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(c => c.ShoppingList).WithMany().HasForeignKey(c => new { c.ShoppingListId, c.UserAccountId })
            .HasPrincipalKey(l => new { l.Id, l.UserAccountId }).OnDelete(DeleteBehavior.Cascade);
        builder.Property(c => c.Version).IsConcurrencyToken();
        builder.ToTable("UserCurrentLists", t => t.HasCheckConstraint("CK_UserCurrentLists_Version", "\"Version\" > 0"));
    }
}
