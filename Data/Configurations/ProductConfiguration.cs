using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("Products", table =>
        {
            table.HasCheckConstraint("CK_Products_PackQuantity", "\"PackQuantity\" IS NULL OR \"PackQuantity\" > 0");
            table.HasCheckConstraint("CK_Products_PackSize", "\"PackSize\" IS NULL OR \"PackSize\" > 0");
        });
        builder.Property(x => x.Name).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Brand).HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(10000);
        builder.Property(x => x.ItemDetail).HasMaxLength(4000);
        builder.Property(x => x.GTIN).HasMaxLength(14);
        builder.Property(x => x.ManufacturerPartNumber).HasMaxLength(200);
        builder.Property(x => x.ModelNumber).HasMaxLength(200);
        builder.Property(x => x.Variant).HasMaxLength(200);
        builder.Property(x => x.PackSize).HasPrecision(18, 4);
        builder.Property(x => x.PackUnit).HasMaxLength(20);
        builder.Property(x => x.Category).HasMaxLength(200);
        builder.Property(x => x.SubCategory).HasMaxLength(200);
        builder.Property(x => x.ImageUrl).HasMaxLength(2048);
        // Contradictory catalogue identities require resolution, not an irreversible GTIN uniqueness rule.
        builder.HasIndex(x => x.GTIN);
        builder.HasIndex(x => new { x.Brand, x.ManufacturerPartNumber, x.ModelNumber });
    }
}

