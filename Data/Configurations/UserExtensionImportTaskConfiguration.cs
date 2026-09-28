using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class UserExtensionImportTaskConfiguration : IEntityTypeConfiguration<UserExtensionImportTask>
{
    public void Configure(EntityTypeBuilder<UserExtensionImportTask> b)
    {
        b.HasKey(t => t.Id);
        b.HasIndex(t => t.ProductImportJobId).IsUnique();
        b.HasIndex(t => t.RequestId).IsUnique();
        b.HasOne(t => t.ProductImportJob).WithMany().HasForeignKey(t => t.ProductImportJobId).OnDelete(DeleteBehavior.Cascade);
        b.Property(t => t.Stage).HasMaxLength(20); b.Property(t => t.Status).HasMaxLength(20);
        b.Property(t => t.Url).HasMaxLength(2048); b.Property(t => t.Query).HasMaxLength(160);
        b.Property(t => t.ErrorCode).HasMaxLength(100);
    }
}
