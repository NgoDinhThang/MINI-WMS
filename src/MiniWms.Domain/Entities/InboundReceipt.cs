using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class InboundReceipt : IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid InboundReceiptId { get; set; } = Guid.NewGuid();

    public string ReceiptCode { get; set; } = string.Empty;

    public Guid WarehouseId { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public string? Note { get; set; }

    public Warehouse? Warehouse { get; set; }

    public User? User { get; set; }

    public ICollection<InboundReceiptDetail> Details { get; } = new List<InboundReceiptDetail>();
}
