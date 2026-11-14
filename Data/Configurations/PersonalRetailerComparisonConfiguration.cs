using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class PersonalRetailerComparisonConfiguration : IEntityTypeConfiguration<PersonalRetailerComparison>
{
    public void Configure(EntityTypeBuilder<PersonalRetailerComparison> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityByDefaultColumn();
        b.HasIndex(x => x.CaptureJobId).IsUnique();
        b.HasIndex(x => x.UserAccountId);
        b.HasOne(x => x.CaptureJob).WithMany().HasForeignKey(x => x.CaptureJobId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.StateJson).IsRequired();
    }
}
