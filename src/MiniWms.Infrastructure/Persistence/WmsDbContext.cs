using MiniWms.Domain.Common;
using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MiniWms.Infrastructure.Persistence;

public sealed class WmsDbContext(
    DbContextOptions<WmsDbContext> options,
    TimeProvider? timeProvider = null) : DbContext(options)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<InboundReceipt> InboundReceipts => Set<InboundReceipt>();
    public DbSet<InboundReceiptDetail> InboundReceiptDetails => Set<InboundReceiptDetail>();
    public DbSet<OutboundReceipt> OutboundReceipts => Set<OutboundReceipt>();
    public DbSet<OutboundReceiptDetail> OutboundReceiptDetails => Set<OutboundReceiptDetail>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        UpdateAuditTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        UpdateAuditTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WmsDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(entityType => typeof(IAuditableEntity).IsAssignableFrom(entityType.ClrType)))
        {
            var builder = modelBuilder.Entity(entityType.ClrType);
            builder.Property<DateTimeOffset>(nameof(IAuditableEntity.CreatedAt))
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .IsRequired();
            builder.Property<DateTimeOffset>(nameof(IAuditableEntity.UpdatedAt))
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .IsRequired();
        }
    }

    private void UpdateAuditTimestamps()
    {
        ChangeTracker.DetectChanges();
        var now = _timeProvider.GetUtcNow().ToUniversalTime();

        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                var createdAt = entry.Property(entity => entity.CreatedAt);
                createdAt.CurrentValue = createdAt.OriginalValue;
                createdAt.IsModified = false;

                var updatedAt = entry.Property(entity => entity.UpdatedAt);
                updatedAt.CurrentValue = now;
                updatedAt.IsModified = true;
            }
        }
    }
}
