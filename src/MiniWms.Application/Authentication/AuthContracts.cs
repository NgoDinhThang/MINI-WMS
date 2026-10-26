using MiniWms.Domain.Entities;

namespace MiniWms.Application.Authentication;

public interface IAuthService
{
    Task<AuthTokens> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<AuthUserDetails> ValidateSessionAsync(AuthSessionIdentity identity, CancellationToken cancellationToken = default);
    Task BanStaffAsync(AuthSessionIdentity actor, Guid targetUserId, bool isBanned, CancellationToken cancellationToken = default);
}

public interface IAuthStore
{
    Task<IAuthTransaction> BeginForEmailAsync(string email, CancellationToken cancellationToken);
    Task<IAuthTransaction> BeginForUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<User?> FindAccountAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Serializes authentication changes for one user across application instances.</summary>
public interface IAuthTransaction : IAsyncDisposable
{
    User? User { get; }
    Task<AuthSession?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<RefreshToken?> FindRefreshTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuthSession>> RevokeSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken);
    void AddSession(AuthSession session);
    void AddRefreshToken(RefreshToken token);
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IAuthSessionStore
{
    Task ReplaceAsync(Guid userId, ActiveAuthSession session, CancellationToken cancellationToken);
    Task<ActiveAuthSession?> GetAsync(Guid userId, CancellationToken cancellationToken);
    Task RemoveIfMatchesAsync(Guid userId, Guid sessionId, long authVersion, CancellationToken cancellationToken);
}

public interface IAuthTokenIssuer
{
    IssuedAccessToken Create(AuthUserDetails user, AuthSession session, DateTimeOffset now, TimeSpan accessTokenLifetime);
}

public interface IRefreshTokenCodec
{
    RefreshTokenMaterial Create(Guid userId);
    PresentedRefreshToken? Parse(string token);
    bool Matches(string suppliedHash, string storedHash);
}

public interface IAuthPasswordVerifier
{
    bool Verify(User? user, string password);
}

public sealed record AuthSessionIdentity(Guid UserId, Guid SessionId, long AuthVersion);
public sealed record AuthUserDetails(Guid UserId, string Email, string FullName, string Role);
public sealed record ActiveAuthSession(Guid SessionId, long AuthVersion, DateTimeOffset ExpiresAt);
public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);
public sealed record RefreshTokenMaterial(string Token, Guid TokenId, string SecretHash);
public sealed record PresentedRefreshToken(Guid UserId, Guid TokenId, string SecretHash);
public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset SessionExpiresAt,
    AuthUserDetails User)
{
    public string TokenType => "Bearer";
}

public sealed record AuthPolicy(TimeSpan AccessTokenLifetime, TimeSpan SessionLifetime)
{
    public static AuthPolicy Default { get; } = new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(8));

    public void Validate()
    {
        if (SessionLifetime <= TimeSpan.Zero || SessionLifetime > TimeSpan.FromHours(8))
        {
            throw new ArgumentOutOfRangeException(nameof(SessionLifetime), "Session lifetime must be positive and no longer than eight hours.");
        }
        if (AccessTokenLifetime <= TimeSpan.Zero || AccessTokenLifetime > TimeSpan.FromHours(8))
        {
            throw new ArgumentOutOfRangeException(nameof(AccessTokenLifetime), "Access token lifetime must be positive and no longer than eight hours.");
        }
    }
}
