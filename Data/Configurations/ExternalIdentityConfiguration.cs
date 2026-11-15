using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class ExternalIdentityConfiguration : IEntityTypeConfiguration<ExternalIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentity> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne(x => x.UserAccount).WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Provider).HasMaxLength(20);
        b.Property(x => x.Subject).HasMaxLength(512).UseCollation("C");
        b.Property(x => x.ProviderEmail).HasMaxLength(320);
        b.HasIndex(x => new { x.Provider, x.Subject }).IsUnique();
        b.HasIndex(x => new { x.UserAccountId, x.Provider }).IsUnique();
        b.HasIndex(x => x.ProviderEmail);
        b.ToTable("ExternalIdentities", t => t.HasCheckConstraint("CK_ExternalIdentity_Provider", "\"Provider\" IN ('google','facebook','microsoft') AND length(\"Subject\") BETWEEN 1 AND 512"));
    }
}
