using MiniWms.Application.Business;
using MiniWms.Domain.Entities;
using MiniWms.Domain.Inventory;
using NUnit.Framework;

namespace MiniWms.Application.Tests;

[TestFixture]
[Category("Unit")]
public sealed class WarehouseServiceTests
{
    private TestStore store = null!;
    private WarehouseService service = null!;
    private readonly Guid productId = Guid.NewGuid();
    private readonly Guid warehouseId = Guid.NewGuid();
    private readonly Guid actorId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        store = new TestStore(warehouseId, productId);
        service = new WarehouseService(store, store);
    }

    [TestCase("-0.001", false, false)]
    [TestCase("0", false, false)]
    [TestCase("0", true, true)]
    [TestCase("0.001", false, true)]
    [TestCase("1.0001", false, false)]
    [TestCase("999999999999999.999", true, true)]
    [TestCase("1000000000000000", true, false)]
    public void Quantity_rules_match_database_precision(string input, bool allowZero, bool expected)
    {
        var quantity = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        Assert.That(StockRules.IsValidQuantity(quantity, allowZero), Is.EqualTo(expected));
    }

    [Test]
    public async Task Product_and_warehouse_codes_are_canonicalized_before_persistence()
    {
        var product = await service.CreateProductAsync(new("  sku-01 ", " Product ", "  details  "), default);
        var warehouse = await service.CreateWarehouseAsync(new(" wh_01 ", " Warehouse ", " Address "), default);
        Assert.Multiple(() =>
        {
            Assert.That(product.SKU, Is.EqualTo("SKU-01"));
            Assert.That(product.Name, Is.EqualTo("Product"));
            Assert.That(warehouse.Code, Is.EqualTo("WH_01"));
            Assert.That(warehouse.Address, Is.EqualTo("Address"));
        });
    }

    [Test]
    public async Task Staff_creation_normalizes_email_and_persists_only_the_hasher_result()
    {
        var result = await service.CreateStaffAsync(new(" NEW@EXAMPLE.TEST ", " New Staff ", "long-initial-password"), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Email, Is.EqualTo("new@example.test"));
            Assert.That(result.Role, Is.EqualTo("Staff"));
            Assert.That(store.SavedUser?.PasswordHash, Is.EqualTo("test-hash"));
            Assert.That(store.SavedUser?.IsActive, Is.True);
        });
    }

    [TestCase("")]
    [TestCase("short")]
    [TestCase("            ")]
    public void Weak_initial_password_is_rejected_before_persistence(string password)
    {
        var exception = Assert.ThrowsAsync<BusinessException>(() =>
            service.CreateStaffAsync(new("new@example.test", "Staff", password), default));
        Assert.That(exception?.Kind, Is.EqualTo(BusinessErrorKind.Validation));
        Assert.That(store.SavedUser, Is.Null);
    }

    [Test]
    public void Duplicate_product_lines_are_rejected_before_opening_a_transaction()
    {
        var exception = Assert.ThrowsAsync<BusinessException>(() => service.CreateInboundAsync(new("IN-1", warehouseId,
            DateTimeOffset.UtcNow, [new(productId, 1), new(productId, 2)]), actorId, default));
        Assert.That(exception?.Kind, Is.EqualTo(BusinessErrorKind.Validation));
        Assert.That(store.BeginCount, Is.Zero);
    }

    [Test]
    public void Missing_dates_and_non_utc_dates_are_rejected()
    {
        foreach (var date in new DateTimeOffset?[] { null, DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(7)) })
        {
            Assert.ThrowsAsync<BusinessException>(() => service.CreateInboundAsync(new("IN-1", warehouseId,
                date, [new(productId, 1)]), actorId, default));
        }
        Assert.That(store.BeginCount, Is.Zero);
    }

    [Test]
    public void A_late_insufficient_line_does_not_modify_earlier_balances_or_create_a_document()
    {
        var otherId = Guid.NewGuid();
        store.Transaction.ProductsMap.Add(otherId, new Product { ProductId = otherId });
        store.Transaction.BalancesMap.Add(otherId, new StockBalance { ProductId = otherId, Quantity = 1 });
        var exception = Assert.ThrowsAsync<BusinessException>(() => service.CreateOutboundAsync(
            new("OUT-1", warehouseId, "Recipient", DateTimeOffset.UtcNow, [new(productId, 2), new(otherId, 3)]),
            actorId, default));
        Assert.Multiple(() =>
        {
            Assert.That(exception?.Kind, Is.EqualTo(BusinessErrorKind.Conflict));
            Assert.That(store.Transaction.Balances[productId].Quantity, Is.EqualTo(10));
            Assert.That(store.Transaction.Outbound, Is.Null);
            Assert.That(store.Transaction.Committed, Is.False);
            Assert.That(store.Transaction.Disposed, Is.True);
        });
    }

    [Test]
    public void Receive_overflow_is_rejected_without_commit()
    {
        store.Transaction.BalancesMap[productId].Quantity = StockRules.MaximumQuantity;
        var exception = Assert.ThrowsAsync<BusinessException>(() => service.CreateInboundAsync(
            new("IN-1", warehouseId, DateTimeOffset.UtcNow, [new(productId, 0.001m)]), actorId, default));
        Assert.That(exception?.Kind, Is.EqualTo(BusinessErrorKind.Conflict));
        Assert.That(store.Transaction.Committed, Is.False);
    }

    [TestCase(0)]
    [TestCase(3)]
    [TestCase(20)]
    public async Task Physical_count_sets_an_absolute_quantity(int count)
    {
        var result = await service.AdjustStockAsync(new(warehouseId, productId, count), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.PreviousQuantity, Is.EqualTo(10));
            Assert.That(result.Quantity, Is.EqualTo(count));
            Assert.That(store.Transaction.Balances[productId].Quantity, Is.EqualTo(count));
            Assert.That(store.Transaction.Committed, Is.True);
        });
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void Inactive_references_reject_movements(bool inactiveWarehouse, bool inactiveProduct)
    {
        store.Transaction.Warehouse.IsActive = !inactiveWarehouse;
        store.Transaction.ProductsMap[productId].IsActive = !inactiveProduct;
        var exception = Assert.ThrowsAsync<BusinessException>(() => service.CreateInboundAsync(
            new("IN-1", warehouseId, DateTimeOffset.UtcNow, [new(productId, 1)]), actorId, default));
        Assert.That(exception?.Kind, Is.EqualTo(BusinessErrorKind.Conflict));
        Assert.That(store.Transaction.Committed, Is.False);
    }

    [TestCase(0, 50)]
    [TestCase(1, 201)]
    [TestCase(int.MaxValue, 200)]
    public void Invalid_pagination_is_rejected(int page, int size)
    {
        Assert.ThrowsAsync<BusinessException>(() => service.ListProductsAsync(null, null, page, size, default));
    }

    private sealed class TestStore(Guid warehouseId, Guid productId) : IWarehouseStore, IStaffPasswordHasher
    {
        public TestTransaction Transaction { get; } = new(warehouseId, productId);
        public int BeginCount { get; private set; }
        public User? SavedUser { get; private set; }
        public string Hash(User user, string password) => "test-hash";
        public Task AddProductAsync(Product product, CancellationToken ct) => Task.CompletedTask;
        public Task AddWarehouseAsync(Warehouse warehouse, CancellationToken ct) => Task.CompletedTask;
        public Task AddStaffAsync(User user, CancellationToken ct) { SavedUser = user; return Task.CompletedTask; }
        public Task<IStockTransaction> BeginStockChangeAsync(Guid warehouse, IReadOnlyCollection<Guid> products, CancellationToken ct)
        {
            BeginCount++;
            return Task.FromResult<IStockTransaction>(Transaction);
        }
        public Task<PageResult<ProductDto>> ListProductsAsync(string? s, bool? a, int p, int n, CancellationToken ct) => throw new NotSupportedException();
        public Task<PageResult<WarehouseDto>> ListWarehousesAsync(string? s, bool? a, int p, int n, CancellationToken ct) => throw new NotSupportedException();
        public Task<PageResult<StockDto>> ListStockAsync(Guid? p, Guid? w, int page, int size, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class TestTransaction(Guid warehouseId, Guid productId) : IStockTransaction
    {
        public Warehouse Warehouse { get; } = new() { WarehouseId = warehouseId };
        public Dictionary<Guid, Product> ProductsMap { get; } = new() { [productId] = new Product { ProductId = productId } };
        public Dictionary<Guid, StockBalance> BalancesMap { get; } = new()
        {
            [productId] = new StockBalance { WarehouseId = warehouseId, ProductId = productId, Quantity = 10 }
        };
        public IReadOnlyDictionary<Guid, Product> Products => ProductsMap;
        public IReadOnlyDictionary<Guid, StockBalance> Balances => BalancesMap;
        public OutboundReceipt? Outbound { get; private set; }
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public void AddBalance(StockBalance balance) => BalancesMap.Add(balance.ProductId, balance);
        public void AddInbound(InboundReceipt receipt) { }
        public void AddOutbound(OutboundReceipt receipt) => Outbound = receipt;
        public Task CommitAsync(CancellationToken ct) { Committed = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
