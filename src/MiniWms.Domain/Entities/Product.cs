using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class Product : IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ProductId { get; set; } = Guid.NewGuid();

    public string SKU { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<StockBalance> StockBalances { get; } = new List<StockBalance>();

    public ICollection<InboundReceiptDetail> InboundReceiptDetails { get; } = new List<InboundReceiptDetail>();

    public ICollection<OutboundReceiptDetail> OutboundReceiptDetails { get; } = new List<OutboundReceiptDetail>();
}
