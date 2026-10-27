using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MiniWms.Application.Authentication;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Persistence;
using Npgsql;

namespace MiniWms.Infrastructure.Authentication;

public sealed class PostgresAuthStore(WmsDbContext context, ILogger<PostgresAuthStore> logger) : IAuthStore
{
    public Task<IAuthTransaction> BeginForEmailAsync(string email, CancellationToken cancellationToken) =>
        BeginAsync(async token =>
        {
            var users = await context.Users.FromSqlInterpolated(
                $"SELECT * FROM \"Users\" WHERE \"Email\" = {email} FOR UPDATE").ToListAsync(token);
            return users.SingleOrDefault();
        }, cancellationToken);

    public Task<IAuthTransaction> BeginForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        BeginAsync(async token =>
        {
            var users = await context.Users.FromSqlInterpolated(
                $"SELECT * FROM \"Users\" WHERE \"UserId\" = {userId} FOR UPDATE").ToListAsync(token);
            return users.SingleOrDefault();
        }, cancellationToken);

    public async Task<User?> FindAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Users.AsNoTracking().Include(user => user.Role)
                .SingleOrDefaultAsync(user => user.UserId == userId, cancellationToken);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            throw Unavailable("read account", exception);
        }
    }

    private async Task<IAuthTransaction> BeginAsync(
        Func<CancellationToken, Task<User?>> loadUser,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException("Authentication transactions require a context without pending writes or another transaction.");
        }
        context.ChangeTracker.Clear();
        IDbContextTransaction? transaction = null;
        var ownershipTransferred = false;
        try
        {
            transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);
            var user = await loadUser(cancellationToken);
            if (user is not null)
            {
                // Keep FOR UPDATE away from a nullable navigation join; fetch the role after locking Users.
                await context.Entry(user).Reference(account => account.Role).LoadAsync(cancellationToken);
            }
            var result = new PostgresAuthTransaction(context, transaction, user, logger);
            ownershipTransferred = true;
            return result;
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            throw Unavailable("lock account", exception);
        }
        finally
        {
            // The adapter owns a successfully returned transaction. Failed setup retains
            // ownership here, including non-database failures and cancellation.
            if (!ownershipTransferred)
            {
                if (transaction is not null)
                {
                    try { await transaction.DisposeAsync(); }
                    catch (Exception cleanupException) when (IsPersistenceFailure(cleanupException))
                    {
                        logger.LogWarning("Authentication transaction cleanup failed ({ExceptionType}).", cleanupException.GetType().Name);
                    }
                }
                context.ChangeTracker.Clear();
            }
        }
    }

    private AuthException Unavailable(string operation, Exception exception)
    {
        logger.LogWarning("Authentication database operation {Operation} failed ({ExceptionType}).", operation, exception.GetType().Name);
        return new AuthException(AuthErrorKind.Infrastructure, "Authentication database is unavailable.", exception);
    }

    internal static bool IsPersistenceFailure(Exception exception) =>
        exception is NpgsqlException or DbUpdateException or TimeoutException ||
        // Npgsql's non-retrying EF execution strategy wraps transient connection
        // failures in InvalidOperationException. Preserve their unavailable status.
        (exception is InvalidOperationException && exception.InnerException is not null &&
            IsPersistenceFailure(exception.InnerException));

    internal sealed class PostgresAuthTransaction(
        WmsDbContext context,
        IDbContextTransaction transaction,
        User? user,
        ILogger logger) : IAuthTransaction
    {
        private bool _committed;
        private bool _disposed;

        public User? User { get; } = user;

        public async Task<AuthSession?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            if (User is null) { return null; }
            return await ExecuteAsync("read authentication session", () => context.Set<AuthSession>()
                .SingleOrDefaultAsync(session => session.SessionId == sessionId && session.UserId == User.UserId, cancellationToken));
        }

        public async Task<RefreshToken?> FindRefreshTokenAsync(Guid tokenId, CancellationToken cancellationToken)
        {
            if (User is null) { return null; }
            return await ExecuteAsync("read refresh token", () => context.Set<RefreshToken>()
                .Include(token => token.Session)
                .SingleOrDefaultAsync(token => token.RefreshTokenId == tokenId &&
                    token.Session != null && token.Session.UserId == User.UserId, cancellationToken));
        }

        public async Task<IReadOnlyList<AuthSession>> RevokeSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (User is null) { return Array.Empty<AuthSession>(); }
            return await ExecuteAsync<IReadOnlyList<AuthSession>>("revoke authentication sessions", async () =>
            {
                var sessions = await context.Set<AuthSession>()
                    .Where(session => session.UserId == User.UserId && session.RevokedAt == null)
                    .ToListAsync(cancellationToken);
                foreach (var session in sessions) { session.RevokedAt = now; }
                // The filtered unique active-session index requires old rows to be updated before a new INSERT.
                // This is only a flush: the caller still commits or rolls back the complete transaction.
                if (sessions.Count > 0) { await context.SaveChangesAsync(cancellationToken); }
                return sessions;
            });
        }

        public void AddSession(AuthSession session) => context.Set<AuthSession>().Add(session);
        public void AddRefreshToken(RefreshToken token) => context.Set<RefreshToken>().Add(token);

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await ExecuteAsync("commit authentication change", async () =>
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _committed = true;
                return true;
            });
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) { return; }
            _disposed = true;
            try
            {
                if (!_committed) { await transaction.RollbackAsync(CancellationToken.None); }
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                logger.LogWarning("Authentication transaction rollback failed ({ExceptionType}).", exception.GetType().Name);
            }
            finally
            {
                try { await transaction.DisposeAsync(); }
                catch (Exception exception) when (IsPersistenceFailure(exception))
                {
                    logger.LogWarning("Authentication transaction disposal failed ({ExceptionType}).", exception.GetType().Name);
                }
                context.ChangeTracker.Clear();
            }
        }

        private async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> action)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { return await action(); }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                logger.LogWarning("Authentication database operation {Operation} failed ({ExceptionType}).", operation, exception.GetType().Name);
                throw new AuthException(AuthErrorKind.Infrastructure, "Authentication database is unavailable.", exception);
            }
        }
    }
}
