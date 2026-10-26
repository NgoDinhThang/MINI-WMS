using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class InboundReceiptDetail : IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid InboundDetailId { get; set; } = Guid.NewGuid();

    public Guid InboundReceiptId { get; set; }

    public Guid ProductId { get; set; }

    public decimal Quantity { get; set; }

    public InboundReceipt? InboundReceipt { get; set; }

    public Product? Product { get; set; }
}
