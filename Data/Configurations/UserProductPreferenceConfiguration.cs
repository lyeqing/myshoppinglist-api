using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class UserProductPreferenceConfiguration : IEntityTypeConfiguration<UserProductPreference>
{
    public void Configure(EntityTypeBuilder<UserProductPreference> builder)
    {
        builder.HasKey(p => new { p.UserAccountId, p.ProductId });
        builder.HasOne(p => p.UserAccount).WithMany().HasForeignKey(p => p.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(p => p.Product).WithMany().HasForeignKey(p => p.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.ProductId);
    }
}
