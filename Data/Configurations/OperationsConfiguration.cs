using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class FeedbackTicketConfiguration : IEntityTypeConfiguration<FeedbackTicket>
{
    public void Configure(EntityTypeBuilder<FeedbackTicket> b)
    {
        b.HasKey(t => t.Id); b.HasOne(t => t.UserAccount).WithMany().HasForeignKey(t => t.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(t => new { t.UserAccountId, t.RequestId }).IsUnique(); b.HasIndex(t => new { t.Status, t.CreatedAt });
        b.Property(t => t.RequestHash).HasMaxLength(64); b.Property(t => t.Category).HasMaxLength(20);
        b.Property(t => t.Message).HasMaxLength(5000); b.Property(t => t.Component).HasMaxLength(20); b.Property(t => t.Release).HasMaxLength(64);
        b.Property(t => t.Status).HasMaxLength(20); b.Property(t => t.Response).HasMaxLength(2000); b.Property(t => t.IssueReference).HasMaxLength(120);
        b.Property(t => t.Version).IsConcurrencyToken();
        b.ToTable("FeedbackTickets", t => {
            t.HasCheckConstraint("CK_FeedbackTickets_Category", "\"Category\" IN ('bug','suggestion','other')");
            t.HasCheckConstraint("CK_FeedbackTickets_Status", "\"Status\" IN ('new','triaged','planned','resolved','closed')");
            t.HasCheckConstraint("CK_FeedbackTickets_Rating", "\"Rating\" IS NULL OR \"Rating\" BETWEEN 1 AND 5");
            t.HasCheckConstraint("CK_FeedbackTickets_Screenshot", "\"Screenshot\" IS NULL OR octet_length(\"Screenshot\")<=500000");
        });
    }
}
public sealed class OperationalEventConfiguration : IEntityTypeConfiguration<OperationalEvent>
{
    public void Configure(EntityTypeBuilder<OperationalEvent> b)
    {
        b.HasKey(e => e.Id); b.HasIndex(e => e.CreatedAt); b.HasIndex(e => new { e.Fingerprint, e.CreatedAt }); b.HasIndex(e => new { e.UserAccountId, e.CreatedAt });
        b.HasOne(e => e.UserAccount).WithMany().HasForeignKey(e => e.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        b.Property(e => e.Component).HasMaxLength(20); b.Property(e => e.Name).HasMaxLength(64); b.Property(e => e.Outcome).HasMaxLength(20);
        b.Property(e => e.Release).HasMaxLength(64); b.Property(e => e.Fingerprint).HasMaxLength(100); b.Property(e => e.TraceId).HasMaxLength(64);
        b.Property(e => e.Attribution).HasMaxLength(80); b.Property(e => e.ClientId).HasMaxLength(100);
    }
}
public sealed class DailyOperationalSummaryConfiguration : IEntityTypeConfiguration<DailyOperationalSummary>
{
    public void Configure(EntityTypeBuilder<DailyOperationalSummary> b)
    {
        b.HasKey(e => new { e.Day, e.Component, e.Name, e.Outcome });
        b.Property(e => e.Component).HasMaxLength(20); b.Property(e => e.Name).HasMaxLength(64); b.Property(e => e.Outcome).HasMaxLength(20);
    }
}
public sealed class OperationalIncidentConfiguration : IEntityTypeConfiguration<OperationalIncident>
{
    public void Configure(EntityTypeBuilder<OperationalIncident> b)
    {
        b.HasKey(e => e.Id); b.HasIndex(e => e.Fingerprint).IsUnique(); b.Property(e => e.Fingerprint).HasMaxLength(100);
        b.Property(e => e.Severity).HasMaxLength(2); b.Property(e => e.Component).HasMaxLength(20); b.Property(e => e.Status).HasMaxLength(20);
        b.Property(e => e.Version).IsConcurrencyToken();
    }
}
public sealed class AlertDeliveryConfiguration : IEntityTypeConfiguration<AlertDelivery>
{
    public void Configure(EntityTypeBuilder<AlertDelivery> b)
    {
        b.HasKey(e => e.Id); b.HasOne(e => e.Incident).WithMany().HasForeignKey(e => e.IncidentId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => new { e.IncidentId, e.IncidentVersion, e.Kind }).IsUnique(); b.HasIndex(e => new { e.Status, e.CreatedAt });
        b.Property(e => e.Kind).HasMaxLength(20); b.Property(e => e.Status).HasMaxLength(20);
    }
}
public sealed class OperationsAdminAuditConfiguration : IEntityTypeConfiguration<OperationsAdminAudit>
{
    public void Configure(EntityTypeBuilder<OperationsAdminAudit> b)
    {
        b.HasKey(e => e.Id); b.HasOne(e => e.Administrator).WithMany().HasForeignKey(e => e.AdministratorId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => e.CreatedAt); b.Property(e => e.Action).HasMaxLength(64); b.Property(e => e.Subject).HasMaxLength(120); b.Property(e => e.Reason).HasMaxLength(1000);
    }
}
