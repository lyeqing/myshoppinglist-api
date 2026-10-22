using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;
namespace myshoppinglist_api.Data.Configurations;
public sealed class ProductBrandSuggestionConfiguration : IEntityTypeConfiguration<ProductBrandSuggestion>
{
    public void Configure(EntityTypeBuilder<ProductBrandSuggestion> b)
    {
        b.HasKey(s => s.Id);
        b.HasIndex(s => new { s.BrandDiscoveryJobId, s.AlternativeProductId }).IsUnique();
        b.HasOne(s => s.BrandDiscoveryJob).WithMany().HasForeignKey(s => s.BrandDiscoveryJobId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(s => s.AlternativeProduct).WithMany().HasForeignKey(s => s.AlternativeProductId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(s => s.ShopProduct).WithMany().HasForeignKey(s => s.ShopProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
