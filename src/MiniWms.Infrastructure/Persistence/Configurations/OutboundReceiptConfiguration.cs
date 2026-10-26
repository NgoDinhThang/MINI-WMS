using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class OutboundReceiptConfiguration : IEntityTypeConfiguration<OutboundReceipt>
{
    public void Configure(EntityTypeBuilder<OutboundReceipt> builder)
    {
        builder.ToTable("OutboundReceipts");
        builder.HasKey(receipt => receipt.OutboundReceiptId);
        builder.Property(receipt => receipt.OutboundReceiptId).ValueGeneratedNever();
        builder.Property(receipt => receipt.ReceiptCode).HasMaxLength(64).IsRequired();
        builder.Property(receipt => receipt.RecipientName).HasMaxLength(200).IsRequired();
        builder.Property(receipt => receipt.IssuedAt).HasColumnType("timestamp with time zone");
        builder.Property(receipt => receipt.Note).HasMaxLength(2000);
        builder.HasIndex(receipt => receipt.ReceiptCode).IsUnique();
        builder.HasIndex(receipt => new { receipt.WarehouseId, receipt.IssuedAt });

        builder.HasOne(receipt => receipt.Warehouse)
            .WithMany(warehouse => warehouse.OutboundReceipts)
            .HasForeignKey(receipt => receipt.WarehouseId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(receipt => receipt.User)
            .WithMany(user => user.OutboundReceipts)
            .HasForeignKey(receipt => receipt.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
