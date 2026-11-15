using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class AccountCredentialChangeConfiguration : IEntityTypeConfiguration<AccountCredentialChange>
{
    public void Configure(EntityTypeBuilder<AccountCredentialChange> b)
    {
        b.HasKey(e => e.UserAccountId);
        b.HasOne(e => e.UserAccount).WithMany().HasForeignKey(e => e.UserAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EmailOutboxConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Kind).HasMaxLength(30);
        b.Property(e => e.Status).HasMaxLength(20);
        b.Property(e => e.RecipientHash).HasMaxLength(64);
        b.Property(e => e.ProtectedPayload).HasMaxLength(24000);
        b.HasIndex(e => new { e.Status, e.NextAttemptAt });
        b.HasIndex(e => new { e.RecipientHash, e.CreatedAt });
        b.ToTable("EmailOutboxMessages", t =>
        {
            t.HasCheckConstraint("CK_EmailOutbox_Kind", "\"Kind\" IN ('account_recovery','password_changed','security_alert','signin_email')");
            t.HasCheckConstraint("CK_EmailOutbox_Status", "\"Status\" IN ('queued','sending','sent','failed','expired','suppressed','cancelled')");
        });
    }
}
