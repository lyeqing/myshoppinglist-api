using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class ColesExtensionTaskConfiguration : IEntityTypeConfiguration<ColesExtensionTask>
{
    public void Configure(EntityTypeBuilder<ColesExtensionTask> b)
    {
        b.HasKey(t => t.Id); b.Property(t => t.Id).UseIdentityByDefaultColumn();
        b.Property(t => t.Key).HasMaxLength(100); b.HasIndex(t => t.Key).IsUnique();
        b.Property(t => t.Kind).HasMaxLength(20); b.Property(t => t.Url).HasMaxLength(2048);
        b.Property(t => t.Query).HasMaxLength(160); b.Property(t => t.Status).HasMaxLength(20);
        b.Property(t => t.WorkerId).HasMaxLength(100); b.Property(t => t.ErrorCode).HasMaxLength(100);
        b.Property(t => t.SubmissionHash).HasMaxLength(64);
        b.HasIndex(t => new { t.Status, t.NextAttemptAt });
        b.Property(t => t.Priority).HasDefaultValue(1).HasSentinel(-1);
        b.HasIndex(t => t.WorkerId).IsUnique().HasFilter("\"Status\" = 'Processing'");
        b.ToTable("ColesExtensionTasks", t =>
        {
            t.HasCheckConstraint("CK_ColesExtensionTasks_Attempts", "\"Attempts\" BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_ColesExtensionTasks_State", "\"Status\" IN ('Waiting','Processing','Completed','Failed')");
        });
    }
}
