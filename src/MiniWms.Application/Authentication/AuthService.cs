using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;
using MiniWms.Domain.Entities;

namespace MiniWms.Application.Authentication;

public sealed class AuthService : IAuthService
{
    private readonly IAuthStore store;
    private readonly IAuthSessionStore sessions;
    private readonly IAuthTokenIssuer accessTokens;
    private readonly IRefreshTokenCodec refreshTokens;
    private readonly IAuthPasswordVerifier passwords;
    private readonly AuthPolicy policy;
    private readonly TimeProvider timeProvider;

    public AuthService(
        IAuthStore store,
        IAuthSessionStore sessions,
        IAuthTokenIssuer accessTokens,
        IRefreshTokenCodec refreshTokens,
        IAuthPasswordVerifier passwords,
        AuthPolicy policy,
        TimeProvider timeProvider)
    {
        policy.Validate();
        this.store = store;
        this.sessions = sessions;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.passwords = passwords;
        this.policy = policy;
        this.timeProvider = timeProvider;
    }

    public async Task<AuthTokens> LoginAsync(
        string email, string password, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = ValidateLogin(email, password);
        await using var transaction = await store.BeginForEmailAsync(normalizedEmail, cancellationToken);
        var user = transaction.User;
        // The verifier performs a dummy verification for missing users as well.
        var passwordMatches = passwords.Verify(user, password);
        if (user is null || !passwordMatches || !user.IsActive || !HasPermittedRole(user))
        {
            throw InvalidCredentials();
        }

        var now = UtcNow();
        await transaction.RevokeSessionsAsync(now, cancellationToken);
        user.AuthVersion = checked(user.AuthVersion + 1);
        var session = new AuthSession
        {
            SessionId = Guid.NewGuid(),
            UserId = user.UserId,
            AuthVersion = user.AuthVersion,
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(now.Add(policy.SessionLifetime).ToUnixTimeSeconds())
        };
        var material = refreshTokens.Create(user.UserId);
        transaction.AddSession(session);
        transaction.AddRefreshToken(NewRefreshToken(material, session));
        var result = CreateTokens(user, session, material, now);
        var activeSession = new ActiveAuthSession(session.SessionId, session.AuthVersion, session.ExpiresAt);

        try
        {
            // A database row lock orders logins across API instances. The Redis write is atomic.
            // Return credentials only after both stores have accepted the new session.
            await sessions.ReplaceAsync(user.UserId, activeSession, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // A timed-out write/commit may already have happened. Remove only this attempted
            // session; never compensate by deleting a newer login's session.
            await RemoveSessionBestEffortAsync(user.UserId, session);
            throw;
        }

        return result;
    }

    public async Task<AuthTokens> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken = default)
    {
        var presented = ParseRefreshToken(refreshToken);
        await using var transaction = await store.BeginForUserAsync(presented.UserId, cancellationToken);
        var user = transaction.User;
        if (user is null || !user.IsActive || !HasPermittedRole(user))
        {
            throw InvalidSession();
        }

        var persistedToken = await transaction.FindRefreshTokenAsync(presented.TokenId, cancellationToken);
        // Never revoke a session merely because a guessed token has the wrong secret.
        if (persistedToken is null || !refreshTokens.Matches(presented.SecretHash, persistedToken.SecretHash))
        {
            throw InvalidSession();
        }
        var session = await transaction.FindSessionAsync(persistedToken.SessionId, cancellationToken);
        var now = UtcNow();
        if (!IsCurrentDurableSession(user, session, now) || persistedToken.ExpiresAt <= now)
        {
            throw InvalidSession();
        }

        if (persistedToken.ConsumedAt is not null)
        {
            // Retaining consumed-token hashes in PostgreSQL detects replay of any earlier
            // token in this family, even after Redis restarts or loses recent writes.
            session.RevokedAt = now;
            user.AuthVersion = checked(user.AuthVersion + 1);
            await transaction.CommitAsync(cancellationToken);
            await RemoveSessionBestEffortAsync(user.UserId, session);
            throw InvalidSession();
        }

        await RequireRedisSessionAsync(user.UserId, session.SessionId, session.AuthVersion, now, cancellationToken);
        var next = refreshTokens.Create(user.UserId);
        var result = CreateTokens(user, session, next, now);
        persistedToken.ConsumedAt = now;
        transaction.AddRefreshToken(NewRefreshToken(next, session));
        await transaction.CommitAsync(cancellationToken);
        // sid, AuthVersion, and Redis's original absolute deadline deliberately do not change.
        return result;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var presented = ParseRefreshToken(refreshToken);
        await using var transaction = await store.BeginForUserAsync(presented.UserId, cancellationToken);
        var user = transaction.User;
        var persistedToken = await transaction.FindRefreshTokenAsync(presented.TokenId, cancellationToken);
        if (user is null || persistedToken is null ||
            !refreshTokens.Matches(presented.SecretHash, persistedToken.SecretHash))
        {
            throw InvalidSession();
        }
        var session = await transaction.FindSessionAsync(persistedToken.SessionId, cancellationToken);
        if (session is null || session.UserId != user.UserId)
        {
            throw InvalidSession();
        }

        // A known old session can be logged out idempotently. It must not revoke the
        // current generation, including after another device has logged in.
        if (session.RevokedAt is not null || session.AuthVersion != user.AuthVersion)
        {
            return;
        }
        session.RevokedAt = UtcNow();
        user.AuthVersion = checked(user.AuthVersion + 1);
        await transaction.CommitAsync(cancellationToken);
        await RemoveSessionBestEffortAsync(user.UserId, session);
    }

    public async Task<AuthUserDetails> ValidateSessionAsync(
        AuthSessionIdentity identity, CancellationToken cancellationToken = default)
    {
        if (identity.UserId == Guid.Empty || identity.SessionId == Guid.Empty || identity.AuthVersion <= 0)
        {
            throw InvalidSession();
        }
        var user = await store.FindAccountAsync(identity.UserId, cancellationToken);
        if (user is null || !user.IsActive || user.AuthVersion != identity.AuthVersion || !HasPermittedRole(user))
        {
            throw InvalidSession();
        }
        await RequireRedisSessionAsync(user.UserId, identity.SessionId, identity.AuthVersion, UtcNow(), cancellationToken);
        return Describe(user);
    }

    public async Task BanStaffAsync(
        AuthSessionIdentity actor, Guid targetUserId, bool isBanned,
        CancellationToken cancellationToken = default)
    {
        var actorAccount = await ValidateSessionAsync(actor, cancellationToken);
        if (actorAccount.Role != "Admin")
        {
            throw new AuthException(AuthErrorKind.Forbidden, "Administrator permission is required.");
        }
        if (targetUserId == Guid.Empty)
        {
            throw Validation("userId", "A nonempty user identifier is required.");
        }
        await using var transaction = await store.BeginForUserAsync(targetUserId, cancellationToken);
        var target = transaction.User ?? throw new AuthException(AuthErrorKind.NotFound, "Staff account was not found.");
        if (target.Role?.Name != "Staff")
        {
            throw new AuthException(AuthErrorKind.Forbidden, "Only Staff accounts can be banned or unbanned.");
        }

        var now = UtcNow();
        var revoked = await transaction.RevokeSessionsAsync(now, cancellationToken);
        target.IsActive = !isBanned;
        target.AuthVersion = checked(target.AuthVersion + 1);
        await transaction.CommitAsync(cancellationToken);
        // Committed account state is authoritative even when Redis cleanup is unavailable.
        foreach (var session in revoked)
        {
            await RemoveSessionBestEffortAsync(target.UserId, session);
        }
    }

    private AuthTokens CreateTokens(User user, AuthSession session, RefreshTokenMaterial refresh, DateTimeOffset now)
    {
        var details = Describe(user);
        var access = accessTokens.Create(details, session, now, policy.AccessTokenLifetime);
        if (access.ExpiresAt <= now || access.ExpiresAt > session.ExpiresAt ||
            access.ExpiresAt > now.Add(policy.AccessTokenLifetime))
        {
            throw new AuthException(AuthErrorKind.Infrastructure, "Token lifetime configuration is invalid.");
        }
        return new AuthTokens(access.Token, access.ExpiresAt, refresh.Token, session.ExpiresAt, details);
    }

    private async Task RequireRedisSessionAsync(
        Guid userId, Guid sessionId, long version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = await sessions.GetAsync(userId, cancellationToken);
        if (current is null || current.SessionId != sessionId || current.AuthVersion != version || current.ExpiresAt <= now)
        {
            throw InvalidSession();
        }
    }

    private async Task RemoveSessionBestEffortAsync(Guid userId, AuthSession session)
    {
        try
        {
            // Redis command timeouts are bounded in Infrastructure. Client cancellation must
            // not skip cleanup after database revocation has already been committed.
            await sessions.RemoveIfMatchesAsync(userId, session.SessionId, session.AuthVersion, CancellationToken.None);
        }
        catch (AuthException exception) when (exception.Kind == AuthErrorKind.Infrastructure)
        {
            // The adapter logs the failure; database version checks still deny this session.
        }
    }

    private PresentedRefreshToken ParseRefreshToken(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 512)
        {
            throw Validation("refreshToken", "A refresh token of at most 512 characters is required.");
        }
        var presented = refreshTokens.Parse(refreshToken);
        if (presented is null || presented.UserId == Guid.Empty || presented.TokenId == Guid.Empty)
        {
            throw InvalidSession();
        }
        return presented;
    }

    private static string ValidateLogin(string email, string password)
    {
        var errors = new Dictionary<string, string[]>();
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 254 || !MailAddress.TryCreate(normalized, out var address) ||
            !string.Equals(address.Address, normalized, StringComparison.Ordinal))
        {
            errors["email"] = new[] { "A valid email address of at most 254 characters is required." };
        }
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024)
        {
            errors["password"] = new[] { "A password of at most 1024 characters is required." };
        }
        if (errors.Count != 0)
        {
            throw new AuthException(AuthErrorKind.Validation, "One or more authentication fields are invalid.") { Errors = errors };
        }
        return normalized;
    }

    private static RefreshToken NewRefreshToken(RefreshTokenMaterial material, AuthSession session) => new()
    {
        RefreshTokenId = material.TokenId,
        SessionId = session.SessionId,
        SecretHash = material.SecretHash,
        ExpiresAt = session.ExpiresAt
    };

    private static bool IsCurrentDurableSession(User user, [NotNullWhen(true)] AuthSession? session, DateTimeOffset now) =>
        session is not null && session.UserId == user.UserId && session.AuthVersion == user.AuthVersion &&
        session.RevokedAt is null && session.ExpiresAt > now;

    private static bool HasPermittedRole(User user) => user.Role?.Name is "Admin" or "Staff";

    private static AuthUserDetails Describe(User user)
    {
        var role = user.Role?.Name;
        if (role is not ("Admin" or "Staff"))
        {
            throw new AuthException(AuthErrorKind.Infrastructure, "The account role could not be loaded.");
        }
        return new AuthUserDetails(user.UserId, user.Email, user.FullName, role);
    }

    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow().ToUniversalTime();
    private static AuthException InvalidCredentials() => new(AuthErrorKind.InvalidCredentials, "The email or password is invalid.");
    private static AuthException InvalidSession() => new(AuthErrorKind.InvalidSession, "The session is invalid or has expired.");
    private static AuthException Validation(string field, string message) =>
        new(AuthErrorKind.Validation, "One or more authentication fields are invalid.")
        {
            Errors = new Dictionary<string, string[]> { [field] = new[] { message } }
        };
}
