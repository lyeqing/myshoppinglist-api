using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;
namespace myshoppinglist_api.Data.Configurations;

public sealed class ContributionObservationConfiguration : IEntityTypeConfiguration<ContributionObservation>
{
    public void Configure(EntityTypeBuilder<ContributionObservation> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.UserAccountId, x.ReceivedAt });
        b.Property(x => x.Url).HasMaxLength(2048);
        b.Property(x => x.Source).HasMaxLength(40);
        b.Property(x => x.ExtensionVersion).HasMaxLength(40);
        b.Property(x => x.Outcome).HasMaxLength(100);
    }
}
