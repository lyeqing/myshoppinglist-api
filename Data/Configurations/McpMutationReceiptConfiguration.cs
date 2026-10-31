using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class McpMutationReceiptConfiguration : IEntityTypeConfiguration<McpMutationReceipt>
{
    public void Configure(EntityTypeBuilder<McpMutationReceipt> b)
    {
        b.HasKey(r => new { r.UserAccountId, r.RequestId });
        b.HasOne(r => r.UserAccount).WithMany().HasForeignKey(r => r.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        b.Property(r => r.Operation).HasMaxLength(80);
        b.Property(r => r.RequestHash).HasMaxLength(64);
        b.Property(r => r.ResultJson).HasMaxLength(10000);
        b.HasIndex(r => r.CreatedDate);
    }
}
