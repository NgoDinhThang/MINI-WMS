using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class OutboundReceipt : IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid OutboundReceiptId { get; set; } = Guid.NewGuid();

    public string ReceiptCode { get; set; } = string.Empty;

    public Guid WarehouseId { get; set; }

    public Guid UserId { get; set; }

    public string RecipientName { get; set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; set; }

    public string? Note { get; set; }

    public Warehouse? Warehouse { get; set; }

    public User? User { get; set; }

    public ICollection<OutboundReceiptDetail> Details { get; } = new List<OutboundReceiptDetail>();
}
