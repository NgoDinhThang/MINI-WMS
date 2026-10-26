using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class OutboundReceiptDetailConfiguration : IEntityTypeConfiguration<OutboundReceiptDetail>
{
    public void Configure(EntityTypeBuilder<OutboundReceiptDetail> builder)
    {
        builder.ToTable("OutboundReceiptDetails", table =>
            table.HasCheckConstraint("CK_OutboundReceiptDetails_Quantity_Positive", "\"Quantity\" > 0"));
        builder.HasKey(detail => detail.OutboundDetailId);
        builder.Property(detail => detail.OutboundDetailId).ValueGeneratedNever();
        builder.Property(detail => detail.Quantity).HasPrecision(18, 3).IsRequired();
        builder.HasIndex(detail => new { detail.OutboundReceiptId, detail.ProductId }).IsUnique();

        builder.HasOne(detail => detail.OutboundReceipt)
            .WithMany(receipt => receipt.Details)
            .HasForeignKey(detail => detail.OutboundReceiptId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(detail => detail.Product)
            .WithMany(product => product.OutboundReceiptDetails)
            .HasForeignKey(detail => detail.ProductId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
