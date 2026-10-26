using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class Role : IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid RoleId { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ICollection<User> Users { get; } = new List<User>();
}
