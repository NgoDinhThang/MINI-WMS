using MiniWms.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MiniWms.Infrastructure.Persistence.Seeding;

public sealed class MockDataSeeder(WmsDbContext context, IPasswordHasher<User> passwordHasher)
{
    public async Task<MockDataSeedResult> SeedAsync(
        MockDataOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (context.ChangeTracker.Entries().Any())
        {
            throw new InvalidOperationException("Development seeding requires a fresh database context.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Serialize seeders; table locks also exclude unrelated writes during the empty-data check.
        await context.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(7241062201)", cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "LOCK TABLE \"InboundReceiptDetails\", \"InboundReceipts\", \"OutboundReceiptDetails\", " +
            "\"OutboundReceipts\", \"Products\", \"Roles\", \"StockBalances\", \"Users\", \"Warehouses\" " +
            "IN SHARE ROW EXCLUSIVE MODE", cancellationToken);

        var roles = await context.Roles.AsNoTracking().ToListAsync(cancellationToken);
        if (roles.Any(role => role.Name is not ("Admin" or "Staff")))
        {
            throw ExistingDataException();
        }

        var fixture = MockDataFixtures.Create(options, passwordHasher, roles);
        if (fixture.Roles.Select(role => role.RoleId).Distinct().Count() != fixture.Roles.Count)
        {
            throw ExistingDataException();
        }

        var counts = await ReadCountsAsync(roles.Count, cancellationToken);
        if (counts == fixture.Counts && await ContainsCompleteFixtureAsync(fixture, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new MockDataSeedResult(true, counts);
        }

        if (new[]
            {
                counts.Users, counts.Products, counts.Warehouses, counts.InboundReceipts,
                counts.InboundReceiptDetails, counts.OutboundReceipts, counts.OutboundReceiptDetails, counts.StockBalances
            }.Any(count => count != 0))
        {
            throw ExistingDataException();
        }

        var existingRoleIds = roles.Select(role => role.RoleId).ToHashSet();
        context.Roles.AddRange(fixture.Roles.Where(role => !existingRoleIds.Contains(role.RoleId)));
        context.Users.AddRange(fixture.Users);
        context.Products.AddRange(fixture.Products);
        context.Warehouses.AddRange(fixture.Warehouses);
        context.InboundReceipts.AddRange(fixture.InboundReceipts);
        context.InboundReceiptDetails.AddRange(fixture.InboundReceiptDetails);
        context.OutboundReceipts.AddRange(fixture.OutboundReceipts);
        context.OutboundReceiptDetails.AddRange(fixture.OutboundReceiptDetails);
        context.StockBalances.AddRange(fixture.StockBalances);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MockDataSeedResult(false, fixture.Counts);
    }

    private async Task<MockDataCounts> ReadCountsAsync(int roleCount, CancellationToken cancellationToken) => new(
        roleCount,
        await context.Users.CountAsync(cancellationToken),
        await context.Products.CountAsync(cancellationToken),
        await context.Warehouses.CountAsync(cancellationToken),
        await context.InboundReceipts.CountAsync(cancellationToken),
        await context.InboundReceiptDetails.CountAsync(cancellationToken),
        await context.OutboundReceipts.CountAsync(cancellationToken),
        await context.OutboundReceiptDetails.CountAsync(cancellationToken),
        await context.StockBalances.CountAsync(cancellationToken));

    private async Task<bool> ContainsCompleteFixtureAsync(MockDataSet fixture, CancellationToken cancellationToken)
    {
        // Compare keys and identifying values only. Reruns never reset passwords, timestamps, or edits.
        var users = await context.Users.AsNoTracking()
            .Select(user => new { user.UserId, user.Email, user.RoleId }).ToListAsync(cancellationToken);
        if (!users.Select(user => (user.UserId, user.Email, user.RoleId)).ToHashSet()
                .SetEquals(fixture.Users.Select(user => (user.UserId, user.Email, user.RoleId))))
        {
            return false;
        }

        var products = await context.Products.AsNoTracking()
            .Select(product => new { product.ProductId, product.SKU }).ToListAsync(cancellationToken);
        if (!products.Select(product => (product.ProductId, product.SKU)).ToHashSet()
                .SetEquals(fixture.Products.Select(product => (product.ProductId, product.SKU))))
        {
            return false;
        }

        var warehouses = await context.Warehouses.AsNoTracking()
            .Select(warehouse => new { warehouse.WarehouseId, warehouse.Code }).ToListAsync(cancellationToken);
        if (!warehouses.Select(warehouse => (warehouse.WarehouseId, warehouse.Code)).ToHashSet()
                .SetEquals(fixture.Warehouses.Select(warehouse => (warehouse.WarehouseId, warehouse.Code))))
        {
            return false;
        }

        var inbounds = await context.InboundReceipts.AsNoTracking()
            .Select(receipt => new { receipt.InboundReceiptId, receipt.ReceiptCode }).ToListAsync(cancellationToken);
        if (!inbounds.Select(receipt => (receipt.InboundReceiptId, receipt.ReceiptCode)).ToHashSet()
                .SetEquals(fixture.InboundReceipts.Select(receipt => (receipt.InboundReceiptId, receipt.ReceiptCode))))
        {
            return false;
        }

        var outbounds = await context.OutboundReceipts.AsNoTracking()
            .Select(receipt => new { receipt.OutboundReceiptId, receipt.ReceiptCode }).ToListAsync(cancellationToken);
        if (!outbounds.Select(receipt => (receipt.OutboundReceiptId, receipt.ReceiptCode)).ToHashSet()
                .SetEquals(fixture.OutboundReceipts.Select(receipt => (receipt.OutboundReceiptId, receipt.ReceiptCode))))
        {
            return false;
        }

        var inboundLines = await context.InboundReceiptDetails.AsNoTracking()
            .Select(line => new { line.InboundDetailId, line.InboundReceiptId, line.ProductId })
            .ToListAsync(cancellationToken);
        if (!inboundLines.Select(line => (line.InboundDetailId, line.InboundReceiptId, line.ProductId)).ToHashSet()
                .SetEquals(fixture.InboundReceiptDetails.Select(line =>
                    (line.InboundDetailId, line.InboundReceiptId, line.ProductId))))
        {
            return false;
        }

        var outboundLines = await context.OutboundReceiptDetails.AsNoTracking()
            .Select(line => new { line.OutboundDetailId, line.OutboundReceiptId, line.ProductId })
            .ToListAsync(cancellationToken);
        if (!outboundLines.Select(line => (line.OutboundDetailId, line.OutboundReceiptId, line.ProductId)).ToHashSet()
                .SetEquals(fixture.OutboundReceiptDetails.Select(line =>
                    (line.OutboundDetailId, line.OutboundReceiptId, line.ProductId))))
        {
            return false;
        }

        var stocks = await context.StockBalances.AsNoTracking()
            .Select(stock => new { stock.WarehouseId, stock.ProductId }).ToListAsync(cancellationToken);
        return stocks.Select(stock => (stock.WarehouseId, stock.ProductId)).ToHashSet()
            .SetEquals(fixture.StockBalances.Select(stock => (stock.WarehouseId, stock.ProductId)));
    }

    private static InvalidOperationException ExistingDataException() => new(
        "Development seed refused: the database contains partial, conflicting, or unrelated data. " +
        "Use an empty development database (existing Admin/Staff roles are allowed), " +
        "or rerun against the complete original fixture. Existing data will not be overwritten or deleted.");
}
