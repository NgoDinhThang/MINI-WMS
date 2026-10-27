using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
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

namespace MiniWms.Api.IntegrationTests;

public sealed partial class AuthApiIntegrationTests
{
    [Test]
    public async Task Persistence_adapters_refuse_an_existing_database_transaction()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var auth = new PostgresAuthStore(context, new Mock<ILogger<PostgresAuthStore>>().Object);
        var stock = new PostgresWarehouseStore(context, new Mock<ILogger<PostgresWarehouseStore>>().Object);
        Assert.ThrowsAsync<InvalidOperationException>(() => auth.BeginForUserAsync(staffId, default));
        Assert.ThrowsAsync<InvalidOperationException>(() => stock.BeginStockChangeAsync(Guid.NewGuid(), [], default));
    }

    [TestCase("none")]
    [TestCase("product")]
    [TestCase("warehouse")]
    public async Task Stock_queries_support_each_filter_over_recorded_pairs(string filter)
    {
        var (warehouse, product) = await SeedStock(2.125m);
        await using var context = CreateContext();
        var store = new PostgresWarehouseStore(context, new Mock<ILogger<PostgresWarehouseStore>>().Object);
        var result = await store.ListStockAsync(filter == "product" ? product.ProductId : null,
            filter == "warehouse" ? warehouse.WarehouseId : null, 1, 50, default);
        Assert.That(result.Total, Is.EqualTo(1));
        Assert.That(result.Items.Single().Quantity, Is.EqualTo(2.125m));
        var staff = await LoginAsync();
        var path = filter == "product" ? "/inventory?productId=" + product.ProductId
            : filter == "warehouse" ? "/inventory?warehouseId=" + warehouse.WarehouseId : "/inventory";
        using var response = await BusinessRequest(Client, staff.AccessToken, path, method: System.Net.Http.HttpMethod.Get);
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
    }

    [Test]
    public async Task Exact_pair_pagination_and_missing_warehouse_are_explicit()
    {
        var (warehouse, product) = await SeedStock(2);
        await using var context = CreateContext();
        var store = new PostgresWarehouseStore(context, new Mock<ILogger<PostgresWarehouseStore>>().Object);
        var result = await store.ListStockAsync(product.ProductId, warehouse.WarehouseId, 2, 1, default);
        Assert.That(result.Total, Is.EqualTo(1));
        Assert.That(result.Items, Is.Empty);
        Assert.That(Assert.ThrowsAsync<BusinessException>(() =>
            store.ListStockAsync(null, Guid.NewGuid(), 1, 50, default))?.Kind, Is.EqualTo(BusinessErrorKind.NotFound));
    }

    [Test]
    public async Task Staff_creation_requires_the_canonical_role_in_the_database()
    {
        await EmptySeedDatabase();
        await using var context = CreateContext();
        var store = new PostgresWarehouseStore(context, new Mock<ILogger<PostgresWarehouseStore>>().Object);
        var user = new User { Email = "new@example.test", FullName = "New Staff", PasswordHash = "unit-hash" };
        Assert.That(Assert.ThrowsAsync<BusinessException>(() => store.AddStaffAsync(user, default))?.Kind,
            Is.EqualTo(BusinessErrorKind.Infrastructure));
        Assert.That(await context.Users.CountAsync(), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task A_database_outage_before_transaction_creation_is_reported_as_unavailability(bool authentication)
    {
        var unavailable = new NpgsqlConnectionStringBuilder(databaseConnection) { Port = 1, Timeout = 1, CommandTimeout = 1, Pooling = false };
        await using var context = new WmsDbContext(new DbContextOptionsBuilder<WmsDbContext>().UseNpgsql(unavailable.ConnectionString).Options);
        if (authentication)
        {
            var store = new PostgresAuthStore(context, new Mock<ILogger<PostgresAuthStore>>().Object);
            Assert.That(Assert.ThrowsAsync<AuthException>(() => store.BeginForUserAsync(staffId, default))?.Kind, Is.EqualTo(AuthErrorKind.Infrastructure));
        }
        else
        {
            var store = new PostgresWarehouseStore(context, new Mock<ILogger<PostgresWarehouseStore>>().Object);
            Assert.That(Assert.ThrowsAsync<BusinessException>(() => store.BeginStockChangeAsync(Guid.NewGuid(), [], default))?.Kind,
                Is.EqualTo(BusinessErrorKind.Infrastructure));
        }
    }

    [TestCase("auth", "none", true)]
    [TestCase("auth", "timeout", true)]
    [TestCase("auth", "unknown", true)]
    [TestCase("auth", "none", false)]
    [TestCase("stock", "none", true)]
    [TestCase("stock", "timeout", true)]
    [TestCase("stock", "unknown", true)]
    [TestCase("stock", "none", false)]
    public async Task Failed_transaction_setup_disposes_its_handle_and_preserves_error_classification(
        string adapter, string cleanup, bool dependency)
    {
        var transaction = new Mock<IDbContextTransaction>(MockBehavior.Strict);
        Exception original = dependency ? new TimeoutException("query failed") : new ArgumentException("query contract");
        if (cleanup == "timeout") { transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.FromException(new TimeoutException("cleanup failed"))); }
        else if (cleanup == "unknown") { transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.FromException(new FormatException("cleanup contract"))); }
        else { transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask); }
        var manager = new Mock<IDbContextTransactionManager>(MockBehavior.Strict);
        manager.SetupGet(m => m.CurrentTransaction).Returns((IDbContextTransaction?)null);
        manager.Setup(m => m.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(transaction.Object);
        manager.Setup(m => m.ResetState()).Callback(() => { });
        manager.Setup(m => m.ResetStateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        using var provider = new ServiceCollection().AddEntityFrameworkNpgsql()
            .AddSingleton(manager.Object).BuildServiceProvider();
        var interceptor = ReaderFailure(original);
        await using var context = new WmsDbContext(new DbContextOptionsBuilder<WmsDbContext>()
            .UseNpgsql(databaseConnection).UseInternalServiceProvider(provider).AddInterceptors(interceptor.Object).Options);
        Func<Task> action = adapter == "auth"
            ? async () => await new PostgresAuthStore(context, new Mock<ILogger<PostgresAuthStore>>().Object).BeginForUserAsync(staffId, default)
            : async () => await new PostgresWarehouseStore(context, new Mock<ILogger<PostgresWarehouseStore>>().Object)
                .BeginStockChangeAsync(Guid.NewGuid(), [], default);
        var expected = cleanup == "unknown" ? typeof(FormatException)
            : !dependency ? typeof(ArgumentException)
            : adapter == "auth" ? typeof(AuthException) : typeof(BusinessException);
        var caught = Assert.ThrowsAsync(expected, async () => await action());
        if (caught is AuthException auth) { Assert.That(auth.Kind, Is.EqualTo(AuthErrorKind.Infrastructure)); }
        if (caught is BusinessException business) { Assert.That(business.Kind, Is.EqualTo(BusinessErrorKind.Infrastructure)); }
        transaction.Verify(t => t.DisposeAsync(), Times.Once);
        Assert.That(context.ChangeTracker.Entries(), Is.Empty);
    }

    [Test]
    public async Task Non_database_account_lookup_failures_are_not_converted_to_503()
    {
        var failure = new ArgumentException("query contract");
        await using var context = new WmsDbContext(new DbContextOptionsBuilder<WmsDbContext>()
            .UseNpgsql(databaseConnection).AddInterceptors(ReaderFailure(failure).Object).Options);
        var store = new PostgresAuthStore(context, new Mock<ILogger<PostgresAuthStore>>().Object);
        Assert.That(Assert.ThrowsAsync<ArgumentException>(() => store.FindAccountAsync(staffId, default)), Is.SameAs(failure));
    }


    [Test]
    public async Task Cancelled_auth_transaction_setup_preserves_cancellation()
    {
        await using var context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new PostgresAuthStore(context, new Mock<ILogger<PostgresAuthStore>>().Object);
        Assert.CatchAsync<OperationCanceledException>(() => store.BeginForUserAsync(staffId, cancellation.Token));
    }

    private static Mock<DbCommandInterceptor> ReaderFailure(Exception failure)
    {
        var interceptor = new Mock<DbCommandInterceptor> { CallBase = true };
        interceptor.Setup(i => i.NonQueryExecutingAsync(It.IsAny<DbCommand>(), It.IsAny<CommandEventData>(),
            It.IsAny<InterceptionResult<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InterceptionResult<int>.SuppressWithResult(0));
        interceptor.Setup(i => i.ReaderExecutingAsync(It.IsAny<DbCommand>(), It.IsAny<CommandEventData>(),
            It.IsAny<InterceptionResult<DbDataReader>>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        return interceptor;
    }
}
