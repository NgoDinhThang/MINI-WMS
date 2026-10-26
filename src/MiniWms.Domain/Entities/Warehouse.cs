using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class Warehouse : IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid WarehouseId { get; set; } = Guid.NewGuid();

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public ICollection<StockBalance> StockBalances { get; } = new List<StockBalance>();

    public ICollection<InboundReceipt> InboundReceipts { get; } = new List<InboundReceipt>();

    public ICollection<OutboundReceipt> OutboundReceipts { get; } = new List<OutboundReceipt>();
}
