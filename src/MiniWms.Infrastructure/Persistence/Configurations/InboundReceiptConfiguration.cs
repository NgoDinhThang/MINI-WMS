using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class InboundReceiptConfiguration : IEntityTypeConfiguration<InboundReceipt>
{
    public void Configure(EntityTypeBuilder<InboundReceipt> builder)
    {
        builder.ToTable("InboundReceipts");
        builder.HasKey(receipt => receipt.InboundReceiptId);
        builder.Property(receipt => receipt.InboundReceiptId).ValueGeneratedNever();
        builder.Property(receipt => receipt.ReceiptCode).HasMaxLength(64).IsRequired();
        builder.Property(receipt => receipt.ReceivedAt).HasColumnType("timestamp with time zone");
        builder.Property(receipt => receipt.Note).HasMaxLength(2000);
        builder.HasIndex(receipt => receipt.ReceiptCode).IsUnique();
        builder.HasIndex(receipt => new { receipt.WarehouseId, receipt.ReceivedAt });

        builder.HasOne(receipt => receipt.Warehouse)
            .WithMany(warehouse => warehouse.InboundReceipts)
            .HasForeignKey(receipt => receipt.WarehouseId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(receipt => receipt.User)
            .WithMany(user => user.InboundReceipts)
            .HasForeignKey(receipt => receipt.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
