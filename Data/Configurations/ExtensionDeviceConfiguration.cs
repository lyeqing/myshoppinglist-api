using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class ExtensionDeviceConfiguration : IEntityTypeConfiguration<ExtensionDevice>
{
    public void Configure(EntityTypeBuilder<ExtensionDevice> b)
    {
        b.HasKey(d => d.Id);
        b.HasAlternateKey(d => new { d.Id, d.UserAccountId });
        b.HasOne(d => d.UserAccount).WithMany().HasForeignKey(d => d.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(d => d.UserSession).WithMany().HasForeignKey(d => new { d.UserSessionId, d.UserAccountId })
            .HasPrincipalKey(s => new { s.Id, s.UserAccountId }).OnDelete(DeleteBehavior.Cascade);
        b.Property(d => d.TokenHash).HasMaxLength(64);
        b.Property(d => d.ExtensionId).HasMaxLength(32);
        b.Property(d => d.ExtensionVersion).HasMaxLength(64);
        b.Property(d => d.SupportedRetailers).HasColumnType("text[]");
        b.HasIndex(d => d.TokenHash).IsUnique();
        b.HasIndex(d => new { d.UserAccountId, d.InstallationId }).IsUnique();
        b.HasIndex(d => new { d.UserAccountId, d.RevokedAt, d.LastSeenAt });
    }
}
