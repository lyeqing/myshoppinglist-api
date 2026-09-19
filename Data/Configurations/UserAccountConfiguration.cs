using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("UserAccounts", table =>
        {
            table.HasCheckConstraint("CK_UserAccounts_Credentials", """("IsTrial" AND "ExpiresDate" IS NOT NULL AND "Email" IS NULL AND "PasswordHash" IS NULL AND "PasswordSalt" IS NULL) OR (NOT "IsTrial" AND "Email" IS NOT NULL AND "PasswordHash" IS NOT NULL AND "PasswordSalt" IS NOT NULL)""");
        });
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.Property(x => x.PasswordHash).HasMaxLength(200);
        builder.Property(x => x.PasswordSalt).HasMaxLength(100);
        builder.Property(x => x.DisplayName).HasMaxLength(200);
        builder.HasIndex(x => x.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL");
        builder.HasIndex(x => new { x.IsTrial, x.ExpiresDate });
    }
}

