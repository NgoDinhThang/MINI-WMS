using System.Data;
using MiniWms.Domain.Common;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace MiniWms.Infrastructure.Tests;

[TestFixture]
[Category("Unit")]
public sealed class WmsAuditTests
{
    private static readonly DateTimeOffset OriginalTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SaveTime = new(2026, 10, 22, 9, 30, 0, TimeSpan.Zero);

    [TestCase(SaveMode.Sync)]
    [TestCase(SaveMode.SyncWithoutAcceptingChanges)]
    [TestCase(SaveMode.Async)]
    [TestCase(SaveMode.AsyncWithoutAcceptingChanges)]
    public async Task Adding_any_entity_stamps_both_fields_with_the_same_utc_save_time(SaveMode mode)
    {
        var clock = new FixedTimeProvider(SaveTime);
        var interceptor = new SuppressWritesInterceptor();
        await using var context = CreateContext(clock, interceptor);
        IAuditableEntity[] entities =
        [
            new AuthSession { UserId = Guid.NewGuid(), AuthVersion = 1, ExpiresAt = SaveTime.AddHours(8) },
            new RefreshToken { SessionId = Guid.NewGuid(), SecretHash = new string('A', 64), ExpiresAt = SaveTime.AddHours(8) },
            new Role { Name = "Staff" },
            new User { RoleId = Guid.NewGuid(), FullName = "Warehouse Staff", Email = "staff@example.test" },
            new Product { SKU = "AUDIT-001", Name = "Audit product" },
            new Warehouse { Code = "AUDIT-WH", Name = "Audit warehouse", Address = "Test address" },
            new StockBalance { WarehouseId = Guid.NewGuid(), ProductId = Guid.NewGuid(), Quantity = 10m },
            new InboundReceipt { WarehouseId = Guid.NewGuid(), UserId = Guid.NewGuid(), ReceiptCode = "AUDIT-IN", ReceivedAt = SaveTime },
            new InboundReceiptDetail { InboundReceiptId = Guid.NewGuid(), ProductId = Guid.NewGuid(), Quantity = 10m },
            new OutboundReceipt { WarehouseId = Guid.NewGuid(), UserId = Guid.NewGuid(), ReceiptCode = "AUDIT-OUT", RecipientName = "Test recipient", IssuedAt = SaveTime },
            new OutboundReceiptDetail { OutboundReceiptId = Guid.NewGuid(), ProductId = Guid.NewGuid(), Quantity = 1m }
        ];

        foreach (var entity in entities)
        {
            entity.CreatedAt = OriginalTime;
            entity.UpdatedAt = OriginalTime.AddDays(1);
            context.Add(entity);
        }

        await SaveAsync(context, mode);

        foreach (var entity in entities)
        {
            Assert.Multiple(() =>
            {
                Assert.That(entity.CreatedAt, Is.EqualTo(SaveTime), entity.GetType().Name);
                Assert.That(entity.UpdatedAt, Is.EqualTo(SaveTime), entity.GetType().Name);
                Assert.That(entity.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
                Assert.That(entity.UpdatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
            });
        }

        Assert.That(clock.ReadCount, Is.EqualTo(1), "One save operation must use one timestamp for all entities.");
        AssertSingleSuppressedSave(context, interceptor, mode);
    }

    [TestCase(SaveMode.Sync)]
    [TestCase(SaveMode.SyncWithoutAcceptingChanges)]
    [TestCase(SaveMode.Async)]
    [TestCase(SaveMode.AsyncWithoutAcceptingChanges)]
    public async Task Modifying_an_entity_refreshes_UpdatedAt_and_restores_its_original_CreatedAt(SaveMode mode)
    {
        var clock = new FixedTimeProvider(SaveTime);
        var interceptor = new SuppressWritesInterceptor();
        await using var context = CreateContext(clock, interceptor);
        var product = new Product
        {
            SKU = "AUDIT-001", Name = "Original name", CreatedAt = OriginalTime, UpdatedAt = OriginalTime
        };
        var stock = new StockBalance
        {
            WarehouseId = Guid.NewGuid(), ProductId = product.ProductId, Quantity = 10m,
            CreatedAt = OriginalTime, UpdatedAt = OriginalTime
        };
        context.AttachRange(product, stock);
        product.Name = "Changed name";
        stock.Quantity = 8m;
        product.CreatedAt = OriginalTime.AddYears(1);
        stock.CreatedAt = OriginalTime.AddYears(1);
        product.UpdatedAt = OriginalTime.AddDays(1);
        stock.UpdatedAt = OriginalTime.AddDays(1);

        await SaveAsync(context, mode);

        foreach (var entity in new IAuditableEntity[] { product, stock })
        {
            Assert.Multiple(() =>
            {
                Assert.That(entity.CreatedAt, Is.EqualTo(OriginalTime), entity.GetType().Name);
                Assert.That(entity.UpdatedAt, Is.EqualTo(SaveTime), entity.GetType().Name);
                Assert.That(context.Entry(entity).Property(nameof(IAuditableEntity.CreatedAt)).IsModified, Is.False);
                Assert.That(context.Entry(entity).Property(nameof(IAuditableEntity.UpdatedAt)).IsModified, Is.True);
            });
        }

        AssertSingleSuppressedSave(context, interceptor, mode);
    }

    [TestCase(SaveMode.Sync)]
    [TestCase(SaveMode.SyncWithoutAcceptingChanges)]
    [TestCase(SaveMode.Async)]
    [TestCase(SaveMode.AsyncWithoutAcceptingChanges)]
    public async Task Unchanged_and_deleted_entities_keep_their_existing_audit_timestamps(SaveMode mode)
    {
        var interceptor = new SuppressWritesInterceptor();
        await using var context = CreateContext(new FixedTimeProvider(SaveTime), interceptor);
        var unchanged = new Product
        {
            SKU = "AUDIT-UNCHANGED", Name = "Unchanged", CreatedAt = OriginalTime, UpdatedAt = OriginalTime.AddHours(1)
        };
        var deleted = new Product
        {
            SKU = "AUDIT-DELETED", Name = "Deleted", CreatedAt = OriginalTime, UpdatedAt = OriginalTime.AddHours(1)
        };
        context.AttachRange(unchanged, deleted);
        context.Remove(deleted);

        await SaveAsync(context, mode);

        foreach (var entity in new[] { unchanged, deleted })
        {
            Assert.Multiple(() =>
            {
                Assert.That(entity.CreatedAt, Is.EqualTo(OriginalTime));
                Assert.That(entity.UpdatedAt, Is.EqualTo(OriginalTime.AddHours(1)));
            });
        }

        Assert.That(context.Entry(unchanged).State, Is.EqualTo(EntityState.Unchanged));
        AssertSingleSuppressedSave(context, interceptor, mode);
    }

    private static WmsDbContext CreateContext(TimeProvider clock, IInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<WmsDbContext>()
            .UseNpgsql("Host=localhost;Database=miniwms_audit_tests;Username=model_only;Password=unused")
            .AddInterceptors(interceptor)
            .Options;

        return new WmsDbContext(options, clock);
    }

    private static Task<int> SaveAsync(WmsDbContext context, SaveMode mode) => mode switch
    {
        SaveMode.Sync => Task.FromResult(context.SaveChanges()),
        SaveMode.SyncWithoutAcceptingChanges => Task.FromResult(context.SaveChanges(acceptAllChangesOnSuccess: false)),
        SaveMode.Async => context.SaveChangesAsync(CancellationToken.None),
        SaveMode.AsyncWithoutAcceptingChanges => context.SaveChangesAsync(acceptAllChangesOnSuccess: false, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown save mode.")
    };

    private static void AssertSingleSuppressedSave(WmsDbContext context, SuppressWritesInterceptor interceptor, SaveMode mode)
    {
        Assert.Multiple(() =>
        {
            Assert.That(interceptor.SyncCalls, Is.EqualTo(mode is SaveMode.Sync or SaveMode.SyncWithoutAcceptingChanges ? 1 : 0));
            Assert.That(interceptor.AsyncCalls, Is.EqualTo(mode is SaveMode.Async or SaveMode.AsyncWithoutAcceptingChanges ? 1 : 0));
            Assert.That(context.Database.GetDbConnection().State, Is.EqualTo(ConnectionState.Closed));
        });
    }

    public enum SaveMode
    {
        Sync,
        SyncWithoutAcceptingChanges,
        Async,
        AsyncWithoutAcceptingChanges
    }

    private sealed class FixedTimeProvider(DateTimeOffset currentTime) : TimeProvider
    {
        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return currentTime;
        }
    }

    private sealed class SuppressWritesInterceptor : SaveChangesInterceptor
    {
        public int SyncCalls { get; private set; }
        public int AsyncCalls { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            SyncCalls++;
            return InterceptionResult<int>.SuppressWithResult(0);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            AsyncCalls++;
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }
    }
}
