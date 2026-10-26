using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("Warehouses");
        builder.HasKey(warehouse => warehouse.WarehouseId);
        builder.Property(warehouse => warehouse.WarehouseId).ValueGeneratedNever();
        builder.Property(warehouse => warehouse.Code).HasMaxLength(64).IsRequired();
        builder.Property(warehouse => warehouse.Name).HasMaxLength(200).IsRequired();
        builder.Property(warehouse => warehouse.Address).HasMaxLength(500).IsRequired();
        builder.Property(warehouse => warehouse.IsActive).IsRequired();
        builder.HasIndex(warehouse => warehouse.Code).IsUnique();
    }
}
