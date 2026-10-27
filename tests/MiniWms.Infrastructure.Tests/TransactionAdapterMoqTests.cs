using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MiniWms.Application.Authentication;
using MiniWms.Application.Business;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Authentication;
using MiniWms.Infrastructure.Business;
using MiniWms.Infrastructure.Persistence;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace MiniWms.Infrastructure.Tests;

[TestFixture]
[Category("Unit")]
public sealed class TransactionAdapterMoqTests
{
    [Test]
    public async Task A_committed_auth_transaction_is_not_rolled_back_and_disposal_is_idempotent()
    {
        await using var context = Context();
        var transaction = Transaction();
        var adapter = new PostgresAuthStore.PostgresAuthTransaction(context, transaction.Object, new User(), new Mock<ILogger>().Object);
        await adapter.CommitAsync(default);
        await adapter.DisposeAsync();
        await adapter.DisposeAsync();
        transaction.Verify(t => t.CommitAsync(default), Times.Once);
        transaction.Verify(t => t.RollbackAsync(It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(t => t.DisposeAsync(), Times.Once);
        Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.CommitAsync(default));
    }

    [Test]
    public async Task An_uncommitted_auth_transaction_rolls_back_and_clears_pending_changes()
    {
        await using var context = Context();
        var transaction = Transaction();
        var adapter = new PostgresAuthStore.PostgresAuthTransaction(context, transaction.Object, new User(), new Mock<ILogger>().Object);
        adapter.AddSession(new AuthSession { UserId = Guid.NewGuid(), AuthVersion = 1 });
        adapter.AddRefreshToken(new RefreshToken { SessionId = Guid.NewGuid(), SecretHash = new string('A', 64) });
        await adapter.DisposeAsync();
        Assert.That(context.ChangeTracker.Entries(), Is.Empty);
        transaction.Verify(t => t.RollbackAsync(CancellationToken.None), Times.Once);
    }

    [Test]
    public async Task Missing_transaction_user_returns_no_owned_records_without_database_access()
    {
        await using var context = Context();
        var transaction = Transaction();
        var adapter = new PostgresAuthStore.PostgresAuthTransaction(context, transaction.Object, null, new Mock<ILogger>().Object);
        Assert.That(await adapter.FindSessionAsync(Guid.NewGuid(), default), Is.Null);
        Assert.That(await adapter.FindRefreshTokenAsync(Guid.NewGuid(), default), Is.Null);
        Assert.That(await adapter.RevokeSessionsAsync(DateTimeOffset.UtcNow, default), Is.Empty);
        Assert.That(context.Database.GetDbConnection().State, Is.EqualTo(ConnectionState.Closed));
        await adapter.DisposeAsync();
    }

    [TestCase("timeout")]
    [TestCase("wrapped")]
    [TestCase("update")]
    public async Task Auth_commit_dependency_failures_are_wrapped_with_the_original_cause(string kind)
    {
        using var context = Context();
        var transaction = Transaction();
        var failure = Failure(kind);
        transaction.Setup(t => t.CommitAsync(default)).ThrowsAsync(failure);
        var adapter = new PostgresAuthStore.PostgresAuthTransaction(context, transaction.Object, new User(), new Mock<ILogger>().Object);
        var error = Assert.ThrowsAsync<AuthException>(() => adapter.CommitAsync(default));
        Assert.That(error?.Kind, Is.EqualTo(AuthErrorKind.Infrastructure));
        Assert.That(error?.InnerException, Is.SameAs(failure));
        await adapter.DisposeAsync();
    }

    [Test]
    public async Task Non_database_commit_failures_are_not_misclassified()
    {
        await using var context = Context();
        var transaction = Transaction();
        var failure = new InvalidOperationException("contract failure");
        transaction.Setup(t => t.CommitAsync(default)).ThrowsAsync(failure);
        var adapter = new PostgresAuthStore.PostgresAuthTransaction(context, transaction.Object, new User(), new Mock<ILogger>().Object);
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => adapter.CommitAsync(default)), Is.SameAs(failure));
        await adapter.DisposeAsync();
    }

    [TestCase("rollback", true)]
    [TestCase("rollback", false)]
    [TestCase("dispose", true)]
    [TestCase("dispose", false)]
    public async Task Auth_cleanup_handles_dependency_errors_and_propagates_unexpected_errors(string operation, bool dependency)
    {
        await using var context = Context();
        var transaction = Transaction();
        Exception failure = dependency ? new TimeoutException() : new ArgumentException("unexpected");
        if (operation == "rollback") { transaction.Setup(t => t.RollbackAsync(CancellationToken.None)).ThrowsAsync(failure); }
        else { transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.FromException(failure)); }
        var logger = new Mock<ILogger>();
        var adapter = new PostgresAuthStore.PostgresAuthTransaction(context, transaction.Object, new User(), logger.Object);
        if (dependency) { await adapter.DisposeAsync(); }
        else { Assert.That(Assert.ThrowsAsync<ArgumentException>(() => adapter.DisposeAsync().AsTask()), Is.SameAs(failure)); }
        transaction.Verify(t => t.DisposeAsync(), Times.Once);
        Assert.That(context.ChangeTracker.Entries(), Is.Empty);
    }

    [TestCase("timeout", true)]
    [TestCase("npgsql", true)]
    [TestCase("update", true)]
    [TestCase("wrapped", true)]
    [TestCase("wrapped-other", false)]
    [TestCase("invalid", false)]
    [TestCase("other", false)]
    public void Database_failure_classification_recognizes_only_supported_dependency_failures(string type, bool expected) =>
        Assert.That(PostgresAuthStore.IsPersistenceFailure(Failure(type)), Is.EqualTo(expected));

    [TestCase(PostgresErrorCodes.LockNotAvailable)]
    [TestCase(PostgresErrorCodes.DeadlockDetected)]
    [TestCase(PostgresErrorCodes.SerializationFailure)]
    [TestCase(PostgresErrorCodes.UniqueViolation)]
    [TestCase(PostgresErrorCodes.ForeignKeyViolation)]
    [TestCase(PostgresErrorCodes.CheckViolation)]
    [TestCase(PostgresErrorCodes.NumericValueOutOfRange)]
    public async Task Stock_commit_maps_constraint_and_concurrency_failures_to_conflict(string sqlState)
    {
        await using var context = Context();
        var transaction = Transaction();
        var postgres = new PostgresException("test constraint", "ERROR", "ERROR", sqlState);
        transaction.Setup(t => t.CommitAsync(default)).ThrowsAsync(new DbUpdateException("test", postgres));
        var owner = new PostgresWarehouseStore(context, new Mock<ILogger<PostgresWarehouseStore>>().Object);
        var adapter = Stock(owner, context, transaction.Object);
        var error = Assert.ThrowsAsync<BusinessException>(() => adapter.CommitAsync(default));
        Assert.That(error?.Kind, Is.EqualTo(BusinessErrorKind.Conflict));
        await adapter.DisposeAsync();
    }

    [TestCase("timeout")]
    [TestCase("wrapped")]
    [TestCase("update")]
    [TestCase("npgsql")]
    public async Task Stock_commit_dependency_errors_are_unavailability_errors(string kind)
    {
        await using var context = Context();
        var transaction = Transaction();
        var failure = Failure(kind);
        transaction.Setup(t => t.CommitAsync(default)).ThrowsAsync(failure);
        var owner = new PostgresWarehouseStore(context, new Mock<ILogger<PostgresWarehouseStore>>().Object);
        var adapter = Stock(owner, context, transaction.Object);
        var error = Assert.ThrowsAsync<BusinessException>(() => adapter.CommitAsync(default));
        Assert.That(error?.Kind, Is.EqualTo(BusinessErrorKind.Infrastructure));
        Assert.That(error?.InnerException, Is.SameAs(failure));
        await adapter.DisposeAsync();
    }

    [Test]
    public async Task Stock_xmin_conflicts_are_business_conflicts()
    {
        await using var context = Context();
        var transaction = Transaction();
        transaction.Setup(t => t.CommitAsync(default)).ThrowsAsync(new DbUpdateConcurrencyException());
        var adapter = Stock(new(context, new Mock<ILogger<PostgresWarehouseStore>>().Object), context, transaction.Object);
        Assert.That(Assert.ThrowsAsync<BusinessException>(() => adapter.CommitAsync(default))?.Kind, Is.EqualTo(BusinessErrorKind.Conflict));
        await adapter.DisposeAsync();
    }

    [TestCase("business")]
    [TestCase("cancel")]
    [TestCase("unknown")]
    public async Task Stock_adapter_preserves_business_cancellation_and_unexpected_failures(string kind)
    {
        await using var context = Context();
        var transaction = Transaction();
        Exception failure = kind switch
        {
            "business" => BusinessException.Invalid("field", "invalid"),
            "cancel" => new OperationCanceledException(),
            _ => new InvalidOperationException("unknown")
        };
        transaction.Setup(t => t.CommitAsync(default)).ThrowsAsync(failure);
        var adapter = Stock(new(context, new Mock<ILogger<PostgresWarehouseStore>>().Object), context, transaction.Object);
        var caught = Assert.ThrowsAsync(failure.GetType(), () => adapter.CommitAsync(default));
        Assert.That(caught, Is.SameAs(failure));
        await adapter.DisposeAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Stock_disposal_swallows_only_dependency_failures(bool dependency)
    {
        await using var context = Context();
        var transaction = Transaction();
        Exception failure = dependency ? new TimeoutException() : new ArgumentException("unexpected");
        transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.FromException(failure));
        var adapter = Stock(new(context, new Mock<ILogger<PostgresWarehouseStore>>().Object), context, transaction.Object);
        if (dependency) { await adapter.DisposeAsync(); }
        else { Assert.That(Assert.ThrowsAsync<ArgumentException>(() => adapter.DisposeAsync().AsTask()), Is.SameAs(failure)); }
    }

    [Test]
    public void Dirty_contexts_cannot_start_nested_application_transactions()
    {
        using var context = Context();
        context.Products.Add(new Product { SKU = "PENDING", Name = "Pending" });
        var auth = new PostgresAuthStore(context, new Mock<ILogger<PostgresAuthStore>>().Object);
        var stock = new PostgresWarehouseStore(context, new Mock<ILogger<PostgresWarehouseStore>>().Object);
        Assert.ThrowsAsync<InvalidOperationException>(() => auth.BeginForUserAsync(Guid.NewGuid(), default));
        Assert.ThrowsAsync<InvalidOperationException>(() => stock.BeginStockChangeAsync(Guid.NewGuid(), [], default));
    }

    private static PostgresWarehouseStore.StockTransaction Stock(PostgresWarehouseStore owner, WmsDbContext context, IDbContextTransaction transaction) =>
        new(owner, context, transaction, new Warehouse(), new Dictionary<Guid, Product>(), new Dictionary<Guid, StockBalance>());

    private static WmsDbContext Context()
    {
        var saves = new Mock<SaveChangesInterceptor> { CallBase = true };
        saves.Setup(i => i.SavingChangesAsync(It.IsAny<DbContextEventData>(), It.IsAny<InterceptionResult<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InterceptionResult<int>.SuppressWithResult(0));
        return new(new DbContextOptionsBuilder<WmsDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unit")
            .AddInterceptors(saves.Object).Options);
    }

    private static Mock<IDbContextTransaction> Transaction()
    {
        var transaction = new Mock<IDbContextTransaction>(MockBehavior.Strict);
        transaction.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        transaction.Setup(t => t.RollbackAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return transaction;
    }

    private static Exception Failure(string kind) => kind switch
    {
        "timeout" => new TimeoutException(),
        "npgsql" => new NpgsqlException("test"),
        "update" => new DbUpdateException("test"),
        "wrapped" => new InvalidOperationException("wrapped", new NpgsqlException("test")),
        "wrapped-other" => new InvalidOperationException("wrapped", new ArgumentException()),
        "invalid" => new InvalidOperationException(),
        _ => new ArgumentException()
    };
}
