using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("UserSessions", table => {
            table.HasCheckConstraint("CK_UserSessions_Latitude", "\"Latitude\" BETWEEN -90 AND 90");
            table.HasCheckConstraint("CK_UserSessions_Longitude", "\"Longitude\" BETWEEN -180 AND 180");
        });
        builder.Property(x => x.Platform).HasMaxLength(20);
        builder.Property(x => x.DeviceType).HasMaxLength(20);
        builder.Property(x => x.DeviceModel).HasMaxLength(200);
        builder.Property(x => x.OsVersion).HasMaxLength(100);
        builder.Property(x => x.AppVersion).HasMaxLength(100);
        builder.Property(x => x.UserAgent).HasMaxLength(512);
        builder.Property(x => x.Latitude).HasPrecision(9, 6);
        builder.Property(x => x.Longitude).HasPrecision(9, 6);
        builder.Property(x => x.LocationAccuracy).HasPrecision(12, 3);
        builder.Property(x => x.TokenHash).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.ExpiresDate);
        builder.HasOne(x => x.UserAccount).WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

