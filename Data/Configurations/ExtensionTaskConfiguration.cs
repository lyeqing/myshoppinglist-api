using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class ExtensionTaskConfiguration : IEntityTypeConfiguration<ExtensionTask>
{
    public void Configure(EntityTypeBuilder<ExtensionTask> b)
    {
        b.HasKey(t => t.Id);
        b.HasOne(t => t.UserAccount).WithMany().HasForeignKey(t => t.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(t => t.AiConnection).WithMany().HasForeignKey(t => new { t.AiConnectionId, t.UserAccountId })
            .HasPrincipalKey(c => new { c.Id, c.UserAccountId }).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(t => t.Device).WithMany().HasForeignKey(t => new { t.DeviceId, t.UserAccountId })
            .HasPrincipalKey(d => new { d.Id, d.UserAccountId }).OnDelete(DeleteBehavior.Cascade);
        b.Property(t => t.RequestHash).HasMaxLength(64);
        b.Property(t => t.ClaimHash).HasMaxLength(64);
        b.Property(t => t.Type).HasMaxLength(16);
        b.Property(t => t.Retailer).HasMaxLength(20);
        b.Property(t => t.Url).HasMaxLength(2000);
        b.Property(t => t.Query).HasMaxLength(100);
        b.Property(t => t.Status).HasMaxLength(32);
        b.Property(t => t.ErrorCode).HasMaxLength(64);
        b.Property(t => t.ResultJson).HasMaxLength(12000);
        b.Property(t => t.CompletionHash).HasMaxLength(64);
        b.HasIndex(t => new { t.UserAccountId, t.AiConnectionId, t.RequestId }).IsUnique();
        b.HasIndex(t => new { t.UserAccountId, t.Status, t.ExpiresAt });
        b.HasIndex(t => new { t.UserAccountId, t.ShoppingListId });
        b.ToTable("ExtensionTasks", t =>
        {
            t.HasCheckConstraint("CK_ExtensionTasks_Type", "\"Type\" IN ('capture','search')");
            t.HasCheckConstraint("CK_ExtensionTasks_Status", "\"Status\" IN ('queued','awaiting_user','acknowledged','running','completed','failed','expired','cancelled')");
            t.HasCheckConstraint("CK_ExtensionTasks_Target", "(\"Type\"='capture' AND \"ShoppingListId\" IS NOT NULL AND \"ShoppingListId\">0 AND \"Quantity\">0) OR (\"Type\"='search' AND \"ShoppingListId\" IS NULL)");
            t.HasCheckConstraint("CK_ExtensionTasks_Expiry", "\"ExpiresAt\">\"CreatedAt\"");
            t.HasCheckConstraint("CK_ExtensionTasks_Completion", "\"Status\"<>'completed' OR (\"ResultJson\" IS NOT NULL AND \"CompletionHash\" IS NOT NULL AND \"DeviceId\" IS NOT NULL)");
        });
    }
}
