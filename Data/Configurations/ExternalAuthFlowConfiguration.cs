using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class ExternalAuthFlowConfiguration : IEntityTypeConfiguration<ExternalAuthFlow>
{
    public void Configure(EntityTypeBuilder<ExternalAuthFlow> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<UserSession>().WithMany().HasForeignKey(x => new { x.UserSessionId, x.UserAccountId })
            .HasPrincipalKey(x => new { x.Id, x.UserAccountId }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_ExternalAuthFlows_UserSessions_Owner");
        b.Property(x => x.Provider).HasMaxLength(20);
        b.Property(x => x.Purpose).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.HasIndex(x => x.ExpiresAt);
        b.HasIndex(x => new { x.UserAccountId, x.ExpiresAt });
        b.ToTable("ExternalAuthFlows", t => t.HasCheckConstraint("CK_ExternalAuthFlow_Purpose", "\"Purpose\" IN ('signin','link','reauthenticate','email','email_change') AND \"ExpiresAt\">\"CreatedAt\" AND ((\"UserAccountId\" IS NULL) = (\"UserSessionId\" IS NULL))"));
    }
}
