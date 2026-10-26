using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MiniWms.Application.Business;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Authentication;
using MiniWms.Infrastructure.Persistence;
using Npgsql;

namespace MiniWms.Infrastructure.Business;

public sealed class PostgresWarehouseStore(WmsDbContext context, ILogger<PostgresWarehouseStore> logger) : IWarehouseStore
{
    public Task<PageResult<ProductDto>> ListProductsAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            var query = context.Products.AsNoTracking().AsQueryable();
            if (isActive.HasValue) { query = query.Where(p => p.IsActive == isActive.Value); }
            if (search is not null)
            {
                var term = search.ToLowerInvariant();
                query = query.Where(p => p.SKU.ToLower().Contains(term) || p.Name.ToLower().Contains(term));
            }
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(p => p.SKU).ThenBy(p => p.ProductId).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new ProductDto(p.ProductId, p.SKU, p.Name, p.Description, p.IsActive)).ToListAsync(ct);
            return new PageResult<ProductDto>(items, total, page, pageSize);
        });

    public Task<PageResult<WarehouseDto>> ListWarehousesAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            var query = context.Warehouses.AsNoTracking().AsQueryable();
            if (isActive.HasValue) { query = query.Where(w => w.IsActive == isActive.Value); }
            if (search is not null)
            {
                var term = search.ToLowerInvariant();
                query = query.Where(w => w.Code.ToLower().Contains(term) || w.Name.ToLower().Contains(term));
            }
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(w => w.Code).ThenBy(w => w.WarehouseId).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(w => new WarehouseDto(w.WarehouseId, w.Code, w.Name, w.Address, w.IsActive)).ToListAsync(ct);
            return new PageResult<WarehouseDto>(items, total, page, pageSize);
        });

    public Task<PageResult<StockDto>> ListStockAsync(Guid? productId, Guid? warehouseId, int page, int pageSize, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            if (productId.HasValue && !await context.Products.AnyAsync(p => p.ProductId == productId.Value, ct))
            {
                throw BusinessException.Missing("Product");
            }
            if (warehouseId.HasValue && !await context.Warehouses.AnyAsync(w => w.WarehouseId == warehouseId.Value, ct))
            {
                throw BusinessException.Missing("Warehouse");
            }
            // An exact existing pair returns zero even before its first stock movement.
            if (productId.HasValue && warehouseId.HasValue)
            {
                var product = await context.Products.AsNoTracking().SingleAsync(p => p.ProductId == productId.Value, ct);
                var warehouse = await context.Warehouses.AsNoTracking().SingleAsync(w => w.WarehouseId == warehouseId.Value, ct);
                var quantity = await context.StockBalances.Where(b => b.ProductId == productId.Value && b.WarehouseId == warehouseId.Value)
                    .Select(b => (decimal?)b.Quantity).SingleOrDefaultAsync(ct) ?? 0;
                IReadOnlyList<StockDto> items = page == 1
                    ? [new StockDto(warehouse.WarehouseId, warehouse.Code, product.ProductId, product.SKU, product.Name, quantity)]
                    : [];
                return new PageResult<StockDto>(items, 1, page, pageSize);
            }
            var query = context.StockBalances.AsNoTracking();
            if (productId.HasValue) { query = query.Where(b => b.ProductId == productId.Value); }
            if (warehouseId.HasValue) { query = query.Where(b => b.WarehouseId == warehouseId.Value); }
            var projected = from balance in query
                            join product in context.Products on balance.ProductId equals product.ProductId
                            join warehouse in context.Warehouses on balance.WarehouseId equals warehouse.WarehouseId
                            select new StockDto(balance.WarehouseId, warehouse.Code, balance.ProductId, product.SKU, product.Name, balance.Quantity);
            var total = await projected.CountAsync(ct);
            var rows = await projected.OrderBy(s => s.WarehouseCode).ThenBy(s => s.SKU)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return new PageResult<StockDto>(rows, total, page, pageSize);
        });

    public Task AddProductAsync(Product product, CancellationToken ct) => ExecuteAsync(async () =>
    {
        if (await context.Products.AnyAsync(p => p.SKU.ToUpper() == product.SKU, ct))
        {
            throw BusinessException.Conflict("The product SKU already exists.");
        }
        context.Products.Add(product);
        await context.SaveChangesAsync(ct);
        return true;
    }, "The product SKU already exists.");

    public Task AddWarehouseAsync(Warehouse warehouse, CancellationToken ct) => ExecuteAsync(async () =>
    {
        if (await context.Warehouses.AnyAsync(w => w.Code.ToUpper() == warehouse.Code, ct))
        {
            throw BusinessException.Conflict("The warehouse code already exists.");
        }
        context.Warehouses.Add(warehouse);
        await context.SaveChangesAsync(ct);
        return true;
    }, "The warehouse code already exists.");

    public Task AddStaffAsync(User user, CancellationToken ct) => ExecuteAsync(async () =>
    {
        if (await context.Users.AnyAsync(u => u.Email.ToLower() == user.Email, ct))
        {
            throw BusinessException.Conflict("The email already exists.");
        }
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Name == "Staff", ct)
            ?? throw new BusinessException(BusinessErrorKind.Infrastructure, "The Staff role is not configured.");
        user.RoleId = role.RoleId;
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);
        return true;
    }, "The email already exists.");

    public async Task<IStockTransaction> BeginStockChangeAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException("Stock operations require a context without pending writes or another transaction.");
        }
        context.ChangeTracker.Clear();
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);
            // This existing parent row also serializes concurrent creation of a missing stock pair.
            var warehouses = await context.Warehouses.FromSqlInterpolated(
                $"SELECT * FROM \"Warehouses\" WHERE \"WarehouseId\" = {warehouseId} FOR UPDATE").ToListAsync(ct);
            var ids = productIds.Distinct().OrderBy(id => id).ToArray();
            var products = await context.Products.FromSqlInterpolated(
                $"SELECT * FROM \"Products\" WHERE \"ProductId\" = ANY ({ids}) ORDER BY \"ProductId\" FOR SHARE").ToListAsync(ct);
            var balances = await context.StockBalances.FromSqlInterpolated(
                $"SELECT *, xmin FROM \"StockBalances\" WHERE \"WarehouseId\" = {warehouseId} AND \"ProductId\" = ANY ({ids}) ORDER BY \"ProductId\" FOR UPDATE").ToListAsync(ct);
            return new StockTransaction(this, context, transaction, warehouses.SingleOrDefault(),
                products.ToDictionary(p => p.ProductId), balances.ToDictionary(b => b.ProductId));
        }
        catch (Exception exception)
        {
            if (transaction is not null) { await DisposeTransactionAsync(transaction); }
            context.ChangeTracker.Clear();
            throw Translate(exception, "The receipt code or stock pair already exists.");
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> action, string conflict = "A unique value already exists.")
    {
        try { return await action(); }
        catch (Exception exception) { throw Translate(exception, conflict); }
    }

    private Exception Translate(Exception exception, string conflict)
    {
        if (exception is BusinessException or OperationCanceledException) { return exception; }
        var postgres = FindPostgres(exception);
        if (exception is DbUpdateConcurrencyException ||
            postgres?.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
        {
            return BusinessException.Conflict("A concurrent stock change prevented this operation. Retry the entire request.");
        }
        if (postgres?.SqlState == PostgresErrorCodes.UniqueViolation) { return BusinessException.Conflict(conflict); }
        if (postgres?.SqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NumericValueOutOfRange)
        {
            return BusinessException.Conflict("The operation conflicts with current data or the supported quantity range.");
        }
        if (PostgresAuthStore.IsPersistenceFailure(exception))
        {
            logger.LogWarning("Warehouse database operation failed ({FailureType}).", exception.GetType().Name);
            return new BusinessException(BusinessErrorKind.Infrastructure, "The warehouse database is temporarily unavailable.", exception);
        }
        return exception;
    }

    private static PostgresException? FindPostgres(Exception? exception) =>
        exception is null ? null : exception as PostgresException ?? FindPostgres(exception.InnerException);

    private async ValueTask DisposeTransactionAsync(IDbContextTransaction transaction)
    {
        try { await transaction.DisposeAsync(); } // Dispose rolls back an uncommitted transaction.
        catch (Exception exception) when (PostgresAuthStore.IsPersistenceFailure(exception))
        {
            logger.LogWarning("Warehouse transaction disposal failed ({FailureType}).", exception.GetType().Name);
        }
    }

    private sealed class StockTransaction(
        PostgresWarehouseStore owner, WmsDbContext context, IDbContextTransaction transaction,
        Warehouse? warehouse, IReadOnlyDictionary<Guid, Product> products, Dictionary<Guid, StockBalance> balances) : IStockTransaction
    {
        public Warehouse? Warehouse { get; } = warehouse;
        public IReadOnlyDictionary<Guid, Product> Products { get; } = products;
        public IReadOnlyDictionary<Guid, StockBalance> Balances => balances;
        public void AddBalance(StockBalance balance)
        {
            context.StockBalances.Add(balance);
            balances.Add(balance.ProductId, balance);
        }
        public void AddInbound(InboundReceipt receipt) => context.InboundReceipts.Add(receipt);
        public void AddOutbound(OutboundReceipt receipt) => context.OutboundReceipts.Add(receipt);
        public Task CommitAsync(CancellationToken ct) => owner.ExecuteAsync(async () =>
        {
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }, "The receipt code already exists for this document type.");

        public async ValueTask DisposeAsync()
        {
            await owner.DisposeTransactionAsync(transaction);
            context.ChangeTracker.Clear();
        }
    }
}
