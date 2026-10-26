using System.Security.Cryptography;
using System.Text;
using MiniWms.Application.Authentication;
using MiniWms.Domain.Entities;

namespace MiniWms.Application.Tests;

internal sealed class AuthTestHarness
{
    public const string Password = "Unit-test-password!";
    public MutableClock Clock { get; } = new();
    public FakeAuthStore Store { get; } = new();
    public FakeAuthSessionStore Sessions { get; } = new();
    public FakeAccessTokenIssuer AccessTokens { get; } = new();
    public FakeRefreshTokenCodec RefreshTokens { get; } = new();
    public User Staff { get; }
    public User Admin { get; }
    public User InactiveStaff { get; }
    public AuthService Service { get; }

    public AuthTestHarness()
    {
        Staff = AddUser("staff@mini-wms.test", "Staff", true);
        Admin = AddUser("admin@mini-wms.test", "Admin", true);
        InactiveStaff = AddUser("inactive@mini-wms.test", "Staff", false);
        Service = CreateService(AuthPolicy.Default);
    }

    public AuthService CreateService(AuthPolicy policy) =>
        new(Store, Sessions, AccessTokens, RefreshTokens, new FakePasswordVerifier(), policy, Clock);

    public AuthSessionIdentity Identity(AuthTokens tokens) => AccessTokens.Identities[tokens.AccessToken];

    private User AddUser(string email, string roleName, bool isActive)
    {
        var role = new Role { RoleId = Guid.NewGuid(), Name = roleName };
        var user = new User
        {
            UserId = Guid.NewGuid(), Email = email, FullName = "Unit Test Account", RoleId = role.RoleId,
            Role = role, IsActive = isActive, PasswordHash = "unit-test-hash"
        };
        Store.Users.Add(user.UserId, user);
        return user;
    }
}

internal sealed class MutableClock : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = new(2026, 10, 26, 2, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan duration) => Now = Now.Add(duration);
}

internal sealed class FakePasswordVerifier : IAuthPasswordVerifier
{
    public bool Verify(User? user, string password) =>
        user is not null && user.PasswordHash == "unit-test-hash" && password == AuthTestHarness.Password;
}

internal sealed class FakeAccessTokenIssuer : IAuthTokenIssuer
{
    public Dictionary<string, AuthSessionIdentity> Identities { get; } = new();
    public IssuedAccessToken Create(AuthUserDetails user, AuthSession session, DateTimeOffset now, TimeSpan lifetime)
    {
        var expiry = now.Add(lifetime) < session.ExpiresAt ? now.Add(lifetime) : session.ExpiresAt;
        expiry = DateTimeOffset.FromUnixTimeSeconds(expiry.ToUnixTimeSeconds());
        var token = "unit-access-" + Guid.NewGuid().ToString("N");
        Identities.Add(token, new AuthSessionIdentity(user.UserId, session.SessionId, session.AuthVersion));
        return new IssuedAccessToken(token, expiry);
    }
}

internal sealed class FakeRefreshTokenCodec : IRefreshTokenCodec
{
    private readonly Dictionary<string, PresentedRefreshToken> issued = new();

    public RefreshTokenMaterial Create(Guid userId)
    {
        var tokenId = Guid.NewGuid();
        var token = "unit-refresh-" + tokenId.ToString("N");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        issued.Add(token, new PresentedRefreshToken(userId, tokenId, hash));
        return new RefreshTokenMaterial(token, tokenId, hash);
    }

    public PresentedRefreshToken? Parse(string token) => issued.GetValueOrDefault(token);
    public bool Matches(string suppliedHash, string storedHash) => suppliedHash == storedHash;

    public string WithWrongSecret(string token)
    {
        var original = issued[token];
        var tampered = token + "-tampered";
        issued[tampered] = original with { SecretHash = new string('F', 64) };
        return tampered;
    }
}

internal sealed class FakeAuthSessionStore : IAuthSessionStore
{
    public Dictionary<Guid, ActiveAuthSession> Current { get; } = new();
    public bool FailRead { get; set; }
    public bool FailReplace { get; set; }
    public bool FailRemove { get; set; }
    public int ReplaceCount { get; private set; }
    public int ReadCount { get; private set; }

    public Task ReplaceAsync(Guid userId, ActiveAuthSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReplaceCount++;
        if (FailReplace) { throw Unavailable(); }
        Current[userId] = session;
        return Task.CompletedTask;
    }

    public Task<ActiveAuthSession?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        if (FailRead) { throw Unavailable(); }
        return Task.FromResult(Current.GetValueOrDefault(userId));
    }

    public Task RemoveIfMatchesAsync(Guid userId, Guid sessionId, long authVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailRemove) { throw Unavailable(); }
        if (Current.TryGetValue(userId, out var session) && session.SessionId == sessionId && session.AuthVersion == authVersion)
        {
            Current.Remove(userId);
        }
        return Task.CompletedTask;
    }

    private static AuthException Unavailable() => new(AuthErrorKind.Infrastructure, "Simulated Redis outage.");
}

/// <summary>Copies state for each transaction, so failed/uncommitted operations cannot leak changes.</summary>
internal sealed class FakeAuthStore : IAuthStore
{
    public Dictionary<Guid, User> Users { get; private set; } = new();
    public Dictionary<Guid, AuthSession> Sessions { get; private set; } = new();
    public Dictionary<Guid, RefreshToken> Tokens { get; private set; } = new();
    public bool FailCommit { get; set; }
    public int CommitCount { get; private set; }
    public string? LastEmailLookup { get; private set; }

    public Task<IAuthTransaction> BeginForEmailAsync(string email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastEmailLookup = email;
        var user = Users.Values.SingleOrDefault(candidate => candidate.Email == email);
        return Task.FromResult<IAuthTransaction>(new Transaction(this, user?.UserId));
    }

    public Task<IAuthTransaction> BeginForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAuthTransaction>(new Transaction(this, userId));
    }

    public Task<User?> FindAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = Users.GetValueOrDefault(userId);
        return Task.FromResult(account is null ? null : Copy(account));
    }

    private static User Copy(User value) => new()
    {
        UserId = value.UserId, RoleId = value.RoleId, FullName = value.FullName,
        Email = value.Email, PasswordHash = value.PasswordHash, IsActive = value.IsActive,
        AuthVersion = value.AuthVersion, CreatedAt = value.CreatedAt, UpdatedAt = value.UpdatedAt,
        Role = value.Role is null ? null : new Role { RoleId = value.Role.RoleId, Name = value.Role.Name }
    };

    private static AuthSession Copy(AuthSession value) => new()
    {
        SessionId = value.SessionId, UserId = value.UserId, AuthVersion = value.AuthVersion,
        ExpiresAt = value.ExpiresAt, RevokedAt = value.RevokedAt,
        CreatedAt = value.CreatedAt, UpdatedAt = value.UpdatedAt
    };

    private static RefreshToken Copy(RefreshToken value) => new()
    {
        RefreshTokenId = value.RefreshTokenId, SessionId = value.SessionId, SecretHash = value.SecretHash,
        ExpiresAt = value.ExpiresAt, ConsumedAt = value.ConsumedAt,
        CreatedAt = value.CreatedAt, UpdatedAt = value.UpdatedAt
    };

    private sealed class Transaction : IAuthTransaction
    {
        private readonly FakeAuthStore owner;
        private readonly Dictionary<Guid, User> users;
        private readonly Dictionary<Guid, AuthSession> sessions;
        private readonly Dictionary<Guid, RefreshToken> tokens;
        public User? User { get; }

        public Transaction(FakeAuthStore owner, Guid? userId)
        {
            this.owner = owner;
            users = owner.Users.ToDictionary(pair => pair.Key, pair => Copy(pair.Value));
            sessions = owner.Sessions.ToDictionary(pair => pair.Key, pair => Copy(pair.Value));
            tokens = owner.Tokens.ToDictionary(pair => pair.Key, pair => Copy(pair.Value));
            User = userId is null ? null : users.GetValueOrDefault(userId.Value);
        }

        public Task<AuthSession?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = sessions.GetValueOrDefault(sessionId);
            return Task.FromResult(session?.UserId == User?.UserId ? session : null);
        }

        public Task<RefreshToken?> FindRefreshTokenAsync(Guid tokenId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = tokens.GetValueOrDefault(tokenId);
            var session = token is null ? null : sessions.GetValueOrDefault(token.SessionId);
            return Task.FromResult(session?.UserId == User?.UserId ? token : null);
        }

        public Task<IReadOnlyList<AuthSession>> RevokeSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revoked = sessions.Values.Where(session => session.UserId == User?.UserId && session.RevokedAt is null).ToArray();
            foreach (var session in revoked) { session.RevokedAt = now; }
            return Task.FromResult<IReadOnlyList<AuthSession>>(revoked);
        }

        public void AddSession(AuthSession session) => sessions.Add(session.SessionId, session);
        public void AddRefreshToken(RefreshToken token) => tokens.Add(token.RefreshTokenId, token);

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.FailCommit) { throw new AuthException(AuthErrorKind.Infrastructure, "Simulated database commit failure."); }
            owner.Users = users;
            owner.Sessions = sessions;
            owner.Tokens = tokens;
            owner.CommitCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
