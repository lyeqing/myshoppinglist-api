using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class ShopLocationConfiguration : IEntityTypeConfiguration<ShopLocation>
{
    public void Configure(EntityTypeBuilder<ShopLocation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("ShopLocations", table =>
        {
            table.HasCheckConstraint("CK_ShopLocations_Latitude", "\"Latitude\" BETWEEN -90 AND 90");
            table.HasCheckConstraint("CK_ShopLocations_Longitude", "\"Longitude\" BETWEEN -180 AND 180");
        });
        builder.Property(x => x.StoreCode).HasMaxLength(100);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Address1).HasMaxLength(300);
        builder.Property(x => x.Address2).HasMaxLength(300);
        builder.Property(x => x.Suburb).HasMaxLength(100);
        builder.Property(x => x.State).HasMaxLength(100);
        builder.Property(x => x.Postcode).HasMaxLength(20);
        builder.Property(x => x.Latitude).HasPrecision(9, 6);
        builder.Property(x => x.Longitude).HasPrecision(9, 6);
        builder.HasIndex(x => new { x.ShopId, x.StoreCode }).IsUnique().HasFilter("\"StoreCode\" IS NOT NULL");
        builder.HasOne(x => x.Shop).WithMany().HasForeignKey(x => x.ShopId).OnDelete(DeleteBehavior.Restrict);
    }
}

