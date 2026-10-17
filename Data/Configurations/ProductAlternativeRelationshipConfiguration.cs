using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Data.Configurations;

public sealed class ProductAlternativeRelationshipConfiguration : IEntityTypeConfiguration<ProductAlternativeRelationship>
{
    public void Configure(EntityTypeBuilder<ProductAlternativeRelationship> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.ToTable("ProductAlternativeRelationships", table =>
        {
            table.HasCheckConstraint("CK_ProductAlternatives_OrderedPair", "\"ProductId\" < \"AlternativeProductId\"");
            table.HasCheckConstraint("CK_ProductAlternatives_Evidence", "length(btrim(\"Evidence\")) > 0");
            table.HasCheckConstraint("CK_ProductAlternatives_Confirmation", "NOT \"Approved\" OR (\"SameUseAndFormatConfirmed\" AND \"DietaryAndCompositionChecked\")");
        });
        builder.Property(x => x.Evidence).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.ProductFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AlternativeFingerprint).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.ProductId, x.AlternativeProductId, x.Id });
        builder.HasIndex(x => new { x.AlternativeProductId, x.ProductId, x.Id });
        builder.HasIndex(x => new { x.ProductId, x.AlternativeProductId, x.PreviousReviewId }).IsUnique().AreNullsDistinct(false);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.AlternativeProduct).WithMany().HasForeignKey(x => x.AlternativeProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReviewerAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductAlternativeRelationship>().WithMany().HasForeignKey(x => x.PreviousReviewId).OnDelete(DeleteBehavior.Restrict);
    }
}
