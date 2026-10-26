using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> builder)
    {
        builder.ToTable("StockBalances", table =>
            table.HasCheckConstraint("CK_StockBalances_Quantity_NonNegative", "\"Quantity\" >= 0"));
        builder.HasKey(balance => new { balance.WarehouseId, balance.ProductId });
        builder.Property(balance => balance.Quantity).HasPrecision(18, 3).IsRequired();
        builder.HasIndex(balance => balance.ProductId);

        // Npgsql maps this shadow concurrency token to PostgreSQL's existing xmin column.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasOne(balance => balance.Warehouse)
            .WithMany(warehouse => warehouse.StockBalances)
            .HasForeignKey(balance => balance.WarehouseId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(balance => balance.Product)
            .WithMany(product => product.StockBalances)
            .HasForeignKey(balance => balance.ProductId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
