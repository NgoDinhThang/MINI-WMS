using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class User : IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid UserId { get; set; } = Guid.NewGuid();

    public Guid RoleId { get; set; }

    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public long AuthVersion { get; set; }

    public ICollection<AuthSession> AuthSessions { get; } = new List<AuthSession>();

    public Role? Role { get; set; }

    public ICollection<InboundReceipt> InboundReceipts { get; } = new List<InboundReceipt>();

    public ICollection<OutboundReceipt> OutboundReceipts { get; } = new List<OutboundReceipt>();
}
