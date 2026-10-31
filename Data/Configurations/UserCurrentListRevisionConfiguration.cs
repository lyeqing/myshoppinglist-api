using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class UserCurrentListRevisionConfiguration : IEntityTypeConfiguration<UserCurrentListRevision>
{
    public void Configure(EntityTypeBuilder<UserCurrentListRevision> b)
    {
        b.HasKey(r => r.UserAccountId);
        b.HasOne(r => r.UserAccount).WithMany().HasForeignKey(r => r.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        b.Property(r => r.Version).IsConcurrencyToken();
        b.ToTable("UserCurrentListRevisions", t => t.HasCheckConstraint("CK_UserCurrentListRevisions_Version", "\"Version\" > 0"));
    }
}
