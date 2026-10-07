using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class RetailerWorkloadStateConfiguration : IEntityTypeConfiguration<RetailerWorkloadState>
{
    public void Configure(EntityTypeBuilder<RetailerWorkloadState> b)
    {
        b.HasKey(s => s.Retailer);
        b.Property(s => s.Retailer).HasMaxLength(20);
        b.Property(s => s.BlockedAt).HasColumnType("timestamp with time zone[]");
        b.ToTable("RetailerWorkloadStates", t =>
        {
            t.HasCheckConstraint("CK_RetailerWorkloadStates_Retailer", "\"Retailer\" IN ('coles', 'woolworths')");
            t.HasCheckConstraint("CK_RetailerWorkloadStates_Probe", "(\"ProbeToken\" IS NULL AND \"ProbeExpiresAt\" IS NULL) OR (\"ProbeToken\" IS NOT NULL AND \"ProbeExpiresAt\" IS NOT NULL AND \"PausedUntil\" IS NOT NULL)");
        });
    }
}
