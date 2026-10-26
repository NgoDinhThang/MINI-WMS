using MiniWms.Domain.Common;

namespace MiniWms.Domain.Entities;

public sealed class RefreshToken : IAuditableEntity
{
    public Guid RefreshTokenId { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string SecretHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public AuthSession? Session { get; set; }
}
