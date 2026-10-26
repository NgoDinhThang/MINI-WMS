using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class OutboundReceiptDetail : IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid OutboundDetailId { get; set; } = Guid.NewGuid();

    public Guid OutboundReceiptId { get; set; }

    public Guid ProductId { get; set; }

    public decimal Quantity { get; set; }

    public OutboundReceipt? OutboundReceipt { get; set; }

    public Product? Product { get; set; }
}
