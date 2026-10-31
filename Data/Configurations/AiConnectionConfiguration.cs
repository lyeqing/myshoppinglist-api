using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;
using OpenIddict.EntityFrameworkCore.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class AiConnectionConfiguration : IEntityTypeConfiguration<AiConnection>
{
    public void Configure(EntityTypeBuilder<AiConnection> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.AuthorizationId).HasMaxLength(100);
        builder.Property(c => c.ClientId).HasMaxLength(200);
        builder.Property(c => c.DisplayName).HasMaxLength(200);
        builder.Property(c => c.GrantedScopes).HasMaxLength(1000);
        builder.HasIndex(c => c.AuthorizationId).IsUnique();
        builder.HasOne(c => c.UserAccount).WithMany().HasForeignKey(c => c.UserAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<OpenIddictEntityFrameworkCoreAuthorization>().WithMany().HasForeignKey(c => c.AuthorizationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.UserAccountId, c.RevokedDate });
    }
}
