using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class ShopLocationReviewConfiguration : IEntityTypeConfiguration<ShopLocationReview>
{
    public void Configure(EntityTypeBuilder<ShopLocationReview> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.Property(x => x.Action).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Evidence).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.SourceReference).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.BeforeJson).HasMaxLength(16000);
        builder.Property(x => x.AfterJson).HasMaxLength(16000).IsRequired();
        builder.ToTable("ShopLocationReviews", table =>
        {
            table.HasCheckConstraint("CK_ShopLocationReviews_Action", "\"Action\" IN ('Created','Updated','Retired','Reactivated')");
            table.HasCheckConstraint("CK_ShopLocationReviews_Evidence", "length(btrim(\"Evidence\")) > 0 AND length(btrim(\"SourceReference\")) > 0");
        });
        builder.HasIndex(x => new { x.ShopLocationId, x.Id });
        builder.HasOne<ShopLocation>().WithMany().HasForeignKey(x => x.ShopLocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReviewerAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
