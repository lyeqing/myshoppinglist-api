using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;
namespace myshoppinglist_api.Data.Configurations;

public sealed class AccountAdminAuditConfiguration : IEntityTypeConfiguration<AccountAdminAudit>
{
    public void Configure(EntityTypeBuilder<AccountAdminAudit> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.AccountId, x.CreatedAt });
        b.Property(x => x.Reason).HasMaxLength(1000);
    }
}
