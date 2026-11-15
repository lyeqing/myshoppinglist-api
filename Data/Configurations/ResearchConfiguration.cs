using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class ResearchConfiguration : IEntityTypeConfiguration<ResearchJob>,
    IEntityTypeConfiguration<ResearchCandidate>, IEntityTypeConfiguration<ResearchIssue>
{
    public void Configure(EntityTypeBuilder<ResearchJob> b)
    {
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.Id, x.UserAccountId });
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Query).HasMaxLength(500);
        b.Property(x => x.WorkflowVersion).HasMaxLength(30);
        b.Property(x => x.Status).HasMaxLength(40);
        b.HasIndex(x => new { x.UserAccountId, x.CreatedAt });
    }
    public void Configure(EntityTypeBuilder<ResearchCandidate> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<ResearchJob>().WithMany().HasForeignKey(x => new { x.ResearchJobId, x.UserAccountId })
            .HasPrincipalKey(x => new { x.Id, x.UserAccountId }).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ResearchJobId, x.Key }).IsUnique();
        b.Property(x => x.Key).HasMaxLength(80);
        b.Property(x => x.ReportJson).HasMaxLength(12000);
        b.Property(x => x.IssuesJson).HasMaxLength(2000);
        b.Property(x => x.Validation).HasMaxLength(40);
        // Selection IDs are historical receipts; deletion never transfers ownership.
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.SelectedProductId).OnDelete(DeleteBehavior.SetNull);
    }
    public void Configure(EntityTypeBuilder<ResearchIssue> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<ResearchJob>().WithMany().HasForeignKey(x => new { x.ResearchJobId, x.UserAccountId })
            .HasPrincipalKey(x => new { x.Id, x.UserAccountId }).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ResearchJobId, x.Retailer, x.Code, x.Target }).IsUnique();
        b.Property(x => x.Target).HasMaxLength(80);
        b.Property(x => x.Retailer).HasMaxLength(40);
        b.Property(x => x.Code).HasMaxLength(80);
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.Property(x => x.Status).HasMaxLength(30);
        b.Property(x => x.Resolution).HasMaxLength(1000);
        b.ToTable(t => t.HasCheckConstraint("CK_ResearchIssue_Revision", "\"Revision\" > 0"));
    }
}
