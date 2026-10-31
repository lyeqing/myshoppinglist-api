using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;
namespace myshoppinglist_api.Data.Configurations;

public sealed class BillingCustomerConfiguration : IEntityTypeConfiguration<BillingCustomer>
{
    public void Configure(EntityTypeBuilder<BillingCustomer> b)
    {
        b.HasKey(x => x.UserAccountId); b.HasIndex(x => x.CustomerId).IsUnique();
        b.Property(x => x.CustomerId).HasMaxLength(200);
        b.HasOne(x => x.UserAccount).WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.SnapshotJson).HasColumnType("jsonb");
    }
}
public sealed class BillingWebhookEventConfiguration : IEntityTypeConfiguration<BillingWebhookEvent>
{
    public void Configure(EntityTypeBuilder<BillingWebhookEvent> b)
    {
        b.HasKey(x => x.Id); b.Property(x => x.Id).HasMaxLength(200); b.Property(x => x.CustomerId).HasMaxLength(200);
        b.Property(x => x.Type).HasMaxLength(100); b.Property(x => x.Status).HasMaxLength(20);
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }); b.HasIndex(x => x.CustomerId);
    }
}
public sealed class BillingCheckoutAttemptConfiguration : IEntityTypeConfiguration<BillingCheckoutAttempt>
{
    public void Configure(EntityTypeBuilder<BillingCheckoutAttempt> b)
    {
        b.HasKey(x => new { x.UserAccountId, x.RequestId }); b.Property(x => x.Plan).HasMaxLength(10); b.Property(x => x.Url).HasMaxLength(2000);
        b.HasOne(x => x.UserAccount).WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
