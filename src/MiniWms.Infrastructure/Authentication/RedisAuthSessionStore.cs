using System.Globalization;
using Microsoft.Extensions.Logging;
using MiniWms.Application.Authentication;
using StackExchange.Redis;

namespace MiniWms.Infrastructure.Authentication;

public sealed class RedisAuthSettings
{
    public string KeyPrefix { get; set; } = "miniwms:auth:";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(KeyPrefix) || KeyPrefix.Length > 128 ||
            KeyPrefix.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '{' or '}'))
        {
            throw new InvalidOperationException("Redis:KeyPrefix must contain 1 to 128 non-whitespace characters without braces.");
        }
    }
}

public sealed class RedisAuthSessionStore(
    IConnectionMultiplexer connection,
    RedisAuthSettings settings,
    TimeProvider clock,
    ILogger<RedisAuthSessionStore> logger) : IAuthSessionStore
{
    // Fixed-width decimal generations are compared as strings to avoid Lua's double precision limit.
    private const string ReplaceScript = "local current = redis.call('GET', KEYS[1]); " +
        "if current then local version = string.match(current, '^[^:]+:([^:]+):'); " +
        "if version and string.len(version) == 20 and version > ARGV[2] then return 0 end end; " +
        "redis.call('SET', KEYS[1], ARGV[1], 'PXAT', ARGV[3]); return 1";
    private const string RemoveScript = "local current = redis.call('GET', KEYS[1]); " +
        "if current and string.sub(current, 1, string.len(ARGV[1])) == ARGV[1] " +
        "then return redis.call('DEL', KEYS[1]) end; return 0";

    public async Task ReplaceAsync(Guid userId, ActiveAuthSession session, CancellationToken cancellationToken)
    {
        if (session.SessionId == Guid.Empty || session.AuthVersion < 0 || session.ExpiresAt <= clock.GetUtcNow())
        {
            throw new AuthException(AuthErrorKind.InvalidSession, "The session is no longer valid. Sign in again.");
        }

        var generation = session.AuthVersion.ToString("D20", CultureInfo.InvariantCulture);
        var deadline = session.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var value = $"{session.SessionId:N}:{generation}:{deadline}";
        await ExecuteAsync("replace session", async () =>
        {
            var result = await connection.GetDatabase().ScriptEvaluateAsync(
                ReplaceScript,
                new RedisKey[] { BuildKey(userId) },
                new RedisValue[] { value, generation, deadline }).WaitAsync(cancellationToken);
            if ((long)result != 1)
            {
                throw new AuthException(AuthErrorKind.InvalidSession, "A newer sign-in replaced this session. Sign in again.");
            }
        });
    }

    public async Task<ActiveAuthSession?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        ActiveAuthSession? session = null;
        await ExecuteAsync("read session", async () =>
        {
            var value = await connection.GetDatabase().StringGetAsync(BuildKey(userId)).WaitAsync(cancellationToken);
            if (value.IsNull)
            {
                return;
            }

            var parts = value.ToString().Split(':');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var sessionId) || sessionId == Guid.Empty ||
                parts[1].Length != 20 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
                version < 0 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            {
                throw new AuthException(AuthErrorKind.Infrastructure, "Authentication session storage is unavailable.");
            }

            DateTimeOffset deadline;
            try
            {
                deadline = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new AuthException(AuthErrorKind.Infrastructure, "Authentication session storage is unavailable.", exception);
            }
            if (deadline > clock.GetUtcNow())
            {
                session = new ActiveAuthSession(sessionId, version, deadline);
            }
        });
        return session;
    }

    public Task RemoveIfMatchesAsync(Guid userId, Guid sessionId, long authVersion, CancellationToken cancellationToken)
    {
        var expectedPrefix = $"{sessionId:N}:{authVersion.ToString("D20", CultureInfo.InvariantCulture)}:";
        return ExecuteAsync("revoke session", async () =>
        {
            await connection.GetDatabase().ScriptEvaluateAsync(
                RemoveScript,
                new RedisKey[] { BuildKey(userId) },
                new RedisValue[] { expectedPrefix }).WaitAsync(cancellationToken);
        });
    }

    private string BuildKey(Guid userId) => settings.KeyPrefix + "session:{" + userId.ToString("N") + "}";

    private async Task ExecuteAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            logger.LogWarning("Authentication Redis operation {Operation} failed ({ExceptionType}).", operation, exception.GetType().Name);
            throw new AuthException(AuthErrorKind.Infrastructure, "Authentication session storage is unavailable.", exception);
        }
    }
}
