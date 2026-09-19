using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class ShopConfiguration : IEntityTypeConfiguration<Shop>
{
    public void Configure(EntityTypeBuilder<Shop> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("Shops");
        builder.Property(x => x.Code).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Website).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Domain).HasMaxLength(253).IsRequired();
        builder.Property(x => x.LogoUrl).HasMaxLength(2048);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => x.Domain).IsUnique();
        var created = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);
        builder.HasData(
            new Shop { Id = 1, Code = "coles", Name = "Coles", Domain = "coles.com.au", Website = "https://www.coles.com.au", CreatedDate = created, UpdatedDate = created },
            new Shop { Id = 2, Code = "woolworths", Name = "Woolworths", Domain = "woolworths.com.au", Website = "https://www.woolworths.com.au", CreatedDate = created, UpdatedDate = created },
            new Shop { Id = 3, Code = "aldi", Name = "ALDI", Domain = "aldi.com.au", Website = "https://www.aldi.com.au", CreatedDate = created, UpdatedDate = created },
            new Shop { Id = 4, Code = "iga", Name = "IGA", Domain = "iga.com.au", Website = "https://www.iga.com.au", CreatedDate = created, UpdatedDate = created },
            new Shop { Id = 5, Code = "foodland", Name = "Foodland", Domain = "foodlandsa.com.au", Website = "https://www.foodlandsa.com.au", CreatedDate = created, UpdatedDate = created });
    }
}

