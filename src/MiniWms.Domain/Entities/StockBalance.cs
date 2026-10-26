using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class StockBalance : IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid WarehouseId { get; set; }

    public Guid ProductId { get; set; }

    public decimal Quantity { get; set; }

    public Warehouse? Warehouse { get; set; }

    public Product? Product { get; set; }
}
