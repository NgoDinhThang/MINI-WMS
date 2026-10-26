using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class InboundReceiptDetailConfiguration : IEntityTypeConfiguration<InboundReceiptDetail>
{
    public void Configure(EntityTypeBuilder<InboundReceiptDetail> builder)
    {
        builder.ToTable("InboundReceiptDetails", table =>
            table.HasCheckConstraint("CK_InboundReceiptDetails_Quantity_Positive", "\"Quantity\" > 0"));
        builder.HasKey(detail => detail.InboundDetailId);
        builder.Property(detail => detail.InboundDetailId).ValueGeneratedNever();
        builder.Property(detail => detail.Quantity).HasPrecision(18, 3).IsRequired();
        builder.HasIndex(detail => new { detail.InboundReceiptId, detail.ProductId }).IsUnique();

        builder.HasOne(detail => detail.InboundReceipt)
            .WithMany(receipt => receipt.Details)
            .HasForeignKey(detail => detail.InboundReceiptId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(detail => detail.Product)
            .WithMany(product => product.InboundReceiptDetails)
            .HasForeignKey(detail => detail.ProductId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
