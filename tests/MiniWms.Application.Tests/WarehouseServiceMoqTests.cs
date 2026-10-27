using MiniWms.Application.Business;
using MiniWms.Domain.Entities;
using MiniWms.Domain.Inventory;
using Moq;
using NUnit.Framework;

namespace MiniWms.Application.Tests;

[TestFixture]
[Category("Unit")]
public sealed class WarehouseServiceMoqTests
{
    private Fixture f = new();

    [SetUp]
    public void Setup() => f = new Fixture();

    [Test]
    public async Task Queries_forward_normalized_filters_and_cancellation_without_modifying_results()
    {
        using var source = new CancellationTokenSource();
        var products = new PageResult<ProductDto>([new(f.P1, "SKU-1", "Product", null, true)], 1, 2, 10);
        var warehouses = new PageResult<WarehouseDto>([new(f.WarehouseId, "WH-1", "Warehouse", "Address", true)], 1, 2, 10);
        var stock = new PageResult<StockDto>([new(f.WarehouseId, "WH-1", f.P1, "SKU-1", "Product", 10)], 1, 2, 10);
        f.Store.Setup(s => s.ListProductsAsync("widget", true, 2, 10, source.Token)).ReturnsAsync(products);
        f.Store.Setup(s => s.ListWarehousesAsync("warehouse", false, 2, 10, source.Token)).ReturnsAsync(warehouses);
        f.Store.Setup(s => s.ListStockAsync(f.P1, f.WarehouseId, 2, 10, source.Token)).ReturnsAsync(stock);
        Assert.That(await f.Service.ListProductsAsync(" widget ", true, 2, 10, source.Token), Is.SameAs(products));
        Assert.That(await f.Service.ListWarehousesAsync(" warehouse ", false, 2, 10, source.Token), Is.SameAs(warehouses));
        Assert.That(await f.Service.ListStockAsync(f.P1, f.WarehouseId, 2, 10, source.Token), Is.SameAs(stock));
        f.Store.Verify(s => s.ListProductsAsync("widget", true, 2, 10, source.Token), Times.Once);
        f.Store.Verify(s => s.ListWarehousesAsync("warehouse", false, 2, 10, source.Token), Times.Once);
        f.Store.Verify(s => s.ListStockAsync(f.P1, f.WarehouseId, 2, 10, source.Token), Times.Once);
        f.Store.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Inventory_query_accepts_either_filter_or_no_filter()
    {
        foreach (var pair in new (Guid?, Guid?)[] { (null, null), (f.P1, null), (null, f.WarehouseId) })
        {
            f.Store.Setup(s => s.ListStockAsync(pair.Item1, pair.Item2, 1, 50, default))
                .ReturnsAsync(new PageResult<StockDto>([], 0, 1, 50));
            Assert.That((await f.Service.ListStockAsync(pair.Item1, pair.Item2, 1, 50, default)).Items, Is.Empty);
        }
    }

    [TestCase(0, 50)]
    [TestCase(1, 0)]
    [TestCase(1, 201)]
    [TestCase(int.MaxValue, 200)]
    public void All_queries_validate_page_bounds_before_accessing_storage(int page, int size)
    {
        Invalid(() => f.Service.ListProductsAsync(null, null, page, size, default));
        Invalid(() => f.Service.ListWarehousesAsync(null, null, page, size, default));
        Invalid(() => f.Service.ListStockAsync(null, null, page, size, default));
        f.Store.VerifyNoOtherCalls();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Inventory_rejects_empty_supplied_identifiers(bool product)
    {
        Invalid(() => f.Service.ListStockAsync(product ? Guid.Empty : null, product ? null : Guid.Empty, 1, 50, default));
        f.Store.VerifyNoOtherCalls();
    }

    [TestCase("a-z_09.", "A-Z_09.")]
    [TestCase("AZ09-_.", "AZ09-_.")]
    public async Task Catalog_creation_normalizes_codes_names_and_optional_description(string supplied, string expected)
    {
        f.Store.Setup(s => s.AddProductAsync(It.IsAny<Product>(), default)).Returns(Task.CompletedTask);
        f.Store.Setup(s => s.AddWarehouseAsync(It.IsAny<Warehouse>(), default)).Returns(Task.CompletedTask);
        var product = await f.Service.CreateProductAsync(new(" " + supplied + " ", " Product ", " text\r\nwith\ttabs ", false), default);
        var warehouse = await f.Service.CreateWarehouseAsync(new(" " + supplied + " ", " Warehouse ", " Address ", false), default);
        Assert.Multiple(() =>
        {
            Assert.That(product.SKU, Is.EqualTo(expected));
            Assert.That(product.Name, Is.EqualTo("Product"));
            Assert.That(product.Description, Is.EqualTo("text\r\nwith\ttabs"));
            Assert.That(product.IsActive, Is.False);
            Assert.That(warehouse.Code, Is.EqualTo(expected));
            Assert.That(warehouse.Name, Is.EqualTo("Warehouse"));
            Assert.That(warehouse.Address, Is.EqualTo("Address"));
            Assert.That(warehouse.IsActive, Is.False);
        });
        f.Store.Verify(s => s.AddProductAsync(It.Is<Product>(p => p.ProductId == product.ProductId && p.SKU == expected && !p.IsActive), default), Times.Once);
        f.Store.Verify(s => s.AddWarehouseAsync(It.Is<Warehouse>(w => w.WarehouseId == warehouse.WarehouseId && w.Code == expected && !w.IsActive), default), Times.Once);
    }

    private static IEnumerable<TestCaseData> InvalidProducts()
    {
        yield return new TestCaseData((CreateProductRequest?)null).SetName("Product_null_body");
        foreach (var sku in new[] { "", " ", "@", "[", "/", ":", "{", "é", "A B", new string('S', 65) })
            yield return new TestCaseData(new CreateProductRequest(sku, "Product")).SetName("Product_invalid_code_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(sku)));
        foreach (var name in new string?[] { null, "", " ", "a\nb", new('N', 201) })
            yield return new TestCaseData(new CreateProductRequest("SKU", name)).SetName("Product_invalid_name_" + (name?.Length ?? -1));
        yield return new TestCaseData(new CreateProductRequest("SKU", "Product", new string('D', 2001))).SetName("Product_overlong_description");
        yield return new TestCaseData(new CreateProductRequest("SKU", "Product", "bad\a")).SetName("Product_control_description");
    }

    [TestCaseSource(nameof(InvalidProducts))]
    public void Invalid_products_never_reach_storage(CreateProductRequest? request)
    {
        Invalid(() => f.Service.CreateProductAsync(request, default));
        f.Store.VerifyNoOtherCalls();
    }

    private static IEnumerable<TestCaseData> InvalidWarehouses()
    {
        yield return new TestCaseData((CreateWarehouseRequest?)null).SetName("Warehouse_null_body");
        yield return new TestCaseData(new CreateWarehouseRequest(null, "Warehouse", "Address")).SetName("Warehouse_missing_code");
        yield return new TestCaseData(new CreateWarehouseRequest("WH", null, "Address")).SetName("Warehouse_missing_name");
        foreach (var address in new string?[] { null, "", " ", "a\nb", new('A', 501) })
            yield return new TestCaseData(new CreateWarehouseRequest("WH", "Warehouse", address)).SetName("Warehouse_invalid_address_" + (address?.Length ?? -1));
    }

    [TestCaseSource(nameof(InvalidWarehouses))]
    public void Invalid_warehouses_never_reach_storage(CreateWarehouseRequest? request)
    {
        Invalid(() => f.Service.CreateWarehouseAsync(request, default));
        f.Store.VerifyNoOtherCalls();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("not-email")]
    [TestCase("Display <user@example.test>")]
    public void Staff_requires_a_canonical_email(string? email)
    {
        Invalid(() => f.Service.CreateStaffAsync(new(email, "Staff", "long-initial-password"), default));
        f.Passwords.VerifyNoOtherCalls();
        f.Store.VerifyNoOtherCalls();
    }

    [Test]
    public void Staff_requires_a_body_name_and_bounded_password()
    {
        Invalid(() => f.Service.CreateStaffAsync(null, default));
        Invalid(() => f.Service.CreateStaffAsync(new("staff@example.test", null, "long-initial-password"), default));
        foreach (var password in new string?[] { null, "", " ", "short", new('P', 129) })
            Invalid(() => f.Service.CreateStaffAsync(new("staff@example.test", "Staff", password), default));
        f.Passwords.VerifyNoOtherCalls();
        f.Store.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Staff_creation_hashes_the_exact_password_and_persists_an_active_account()
    {
        User? saved = null;
        f.Passwords.Setup(p => p.Hash(It.Is<User>(u => u.Email == "staff@example.test" && u.IsActive), "  initial-password  ")).Returns("hashed-only");
        f.Store.Setup(s => s.AddStaffAsync(It.IsAny<User>(), default)).Callback<User, CancellationToken>((u, _) => saved = u).Returns(Task.CompletedTask);
        var result = await f.Service.CreateStaffAsync(new(" STAFF@EXAMPLE.TEST ", " Staff ", "  initial-password  "), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Role, Is.EqualTo("Staff"));
            Assert.That(result.FullName, Is.EqualTo("Staff"));
            Assert.That(result.IsActive, Is.True);
            Assert.That(saved?.PasswordHash, Is.EqualTo("hashed-only"));
            Assert.That(saved?.UserId, Is.EqualTo(result.UserId));
        });
        f.Passwords.VerifyAll();
        f.Store.Verify(s => s.AddStaffAsync(It.IsAny<User>(), default), Times.Once);
        f.Store.VerifyNoOtherCalls();
    }

    [TestCase("body")]
    [TestCase("code")]
    [TestCase("warehouse")]
    [TestCase("actor")]
    [TestCase("date-null")]
    [TestCase("date-default")]
    [TestCase("date-offset")]
    [TestCase("lines-null")]
    [TestCase("lines-empty")]
    [TestCase("lines-many")]
    [TestCase("null-line")]
    [TestCase("product-empty")]
    [TestCase("duplicate")]
    [TestCase("quantity-zero")]
    [TestCase("quantity-negative")]
    [TestCase("quantity-precision")]
    [TestCase("quantity-range")]
    [TestCase("note-long")]
    [TestCase("note-control")]
    public void Both_movement_functions_reject_invalid_documents_before_beginning_a_transaction(string defect)
    {
        string? code = defect == "code" ? null : "DOC-1";
        var warehouse = defect == "warehouse" ? Guid.Empty : f.WarehouseId;
        var actor = defect == "actor" ? Guid.Empty : f.ActorId;
        DateTimeOffset? date = defect switch
        {
            "date-null" => null,
            "date-default" => default(DateTimeOffset),
            "date-offset" => f.Date.ToOffset(TimeSpan.FromHours(7)),
            _ => f.Date
        };
        IReadOnlyList<MovementLine?>? lines = defect switch
        {
            "lines-null" => null,
            "lines-empty" => [],
            "lines-many" => Enumerable.Range(0, 101).Select(_ => (MovementLine?)new MovementLine(Guid.NewGuid(), 1)).ToArray(),
            "null-line" => [null],
            "product-empty" => [new(Guid.Empty, 1)],
            "duplicate" => [new(f.P1, 1), new(f.P1, 2)],
            "quantity-zero" => [new(f.P1, 0)],
            "quantity-negative" => [new(f.P1, -1)],
            "quantity-precision" => [new(f.P1, 0.0001m)],
            "quantity-range" => [new(f.P1, 1000000000000000m)],
            _ => [new(f.P1, 2)]
        };
        var note = defect == "note-long" ? new string('N', 2001) : defect == "note-control" ? "bad\a" : null;
        var inbound = defect == "body" ? null : new CreateInboundRequest(code, warehouse, date, lines, note);
        var outbound = defect == "body" ? null : new CreateOutboundRequest(code, warehouse, "Recipient", date, lines, note);
        Invalid(() => f.Service.CreateInboundAsync(inbound, actor, default));
        Invalid(() => f.Service.CreateOutboundAsync(outbound, actor, default));
        f.Store.VerifyNoOtherCalls();
        f.Transaction.VerifyNoOtherCalls();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void Outbound_requires_a_recipient_before_opening_the_transaction(string? recipient)
    {
        Invalid(() => f.Service.CreateOutboundAsync(new("OUT-1", f.WarehouseId, recipient, f.Date, [new(f.P1, 2)]), f.ActorId, default));
        f.Store.VerifyNoOtherCalls();
    }

    [TestCase("warehouse-missing", BusinessErrorKind.NotFound)]
    [TestCase("warehouse-inactive", BusinessErrorKind.Conflict)]
    [TestCase("product-missing", BusinessErrorKind.NotFound)]
    [TestCase("product-inactive", BusinessErrorKind.Conflict)]
    public void Movements_revalidate_all_references_inside_the_transaction(string defect, BusinessErrorKind expected)
    {
        if (defect == "warehouse-missing") { f.Warehouse = null; }
        if (defect == "warehouse-inactive") { f.Warehouse!.IsActive = false; }
        if (defect == "product-missing") { f.Products.Remove(f.P1); }
        if (defect == "product-inactive") { f.Products[f.P1].IsActive = false; }
        Error(() => f.Service.CreateInboundAsync(new("IN-1", f.WarehouseId, f.Date, [new(f.P1, 2)]), f.ActorId, default), expected);
        f.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        f.Transaction.Verify(t => t.DisposeAsync(), Times.Once);
    }

    [Test]
    public async Task Inbound_updates_existing_pairs_and_creates_missing_pairs_in_one_commit()
    {
        var result = await f.Service.CreateInboundAsync(new(" in-1 ", f.WarehouseId, f.Date,
            [new(f.P1, 2.125m), new(f.P2, 3)], "note"), f.ActorId, default);
        Assert.Multiple(() =>
        {
            Assert.That(f.Balances[f.P1].Quantity, Is.EqualTo(12.125m));
            Assert.That(f.Balances[f.P2].Quantity, Is.EqualTo(3));
            Assert.That(result.ReceiptCode, Is.EqualTo("IN-1"));
            Assert.That(result.UserId, Is.EqualTo(f.ActorId));
            Assert.That(result.RecipientName, Is.Null);
            Assert.That(result.DocumentDate, Is.EqualTo(f.Date));
            Assert.That(f.Inbound?.Details.Count, Is.EqualTo(2));
            Assert.That(f.Inbound?.Details.All(d => d.InboundReceiptId == result.ReceiptId), Is.True);
        });
        f.Transaction.Verify(t => t.AddBalance(It.Is<StockBalance>(b => b.ProductId == f.P2 && b.WarehouseId == f.WarehouseId)), Times.Once);
        f.Transaction.Verify(t => t.CommitAsync(default), Times.Once);
        f.Transaction.Verify(t => t.DisposeAsync(), Times.Once);
    }

    [Test]
    public async Task Outbound_updates_all_lines_and_persists_the_authenticated_actor_and_recipient()
    {
        f.Balances.Add(f.P2, new StockBalance { ProductId = f.P2, WarehouseId = f.WarehouseId, Quantity = 7 });
        var result = await f.Service.CreateOutboundAsync(new(" out-1 ", f.WarehouseId, " Recipient ", f.Date,
            [new(f.P1, 2), new(f.P2, 3)], "note"), f.ActorId, default);
        Assert.Multiple(() =>
        {
            Assert.That(f.Balances[f.P1].Quantity, Is.EqualTo(8));
            Assert.That(f.Balances[f.P2].Quantity, Is.EqualTo(4));
            Assert.That(result.RecipientName, Is.EqualTo("Recipient"));
            Assert.That(result.UserId, Is.EqualTo(f.ActorId));
            Assert.That(result.Note, Is.EqualTo("note"));
            Assert.That(f.Outbound?.Details.All(d => d.OutboundReceiptId == result.ReceiptId), Is.True);
            Assert.That(f.Outbound?.Details.Count, Is.EqualTo(2));
        });
        f.Transaction.Verify(t => t.AddOutbound(It.IsAny<OutboundReceipt>()), Times.Once);
        f.Transaction.Verify(t => t.CommitAsync(default), Times.Once);
    }

    [Test]
    public void An_unrecorded_pair_cannot_be_issued()
    {
        Error(() => f.Service.CreateOutboundAsync(new("OUT-1", f.WarehouseId, "Recipient", f.Date, [new(f.P2, 1)]), f.ActorId, default), BusinessErrorKind.Conflict);
        f.Transaction.Verify(t => t.AddOutbound(It.IsAny<OutboundReceipt>()), Times.Never);
        f.Transaction.Verify(t => t.CommitAsync(default), Times.Never);
    }

    [Test]
    public void Adjustment_requires_a_body_pair_and_a_representable_nonnegative_count()
    {
        Invalid(() => f.Service.AdjustStockAsync(null, default));
        Invalid(() => f.Service.AdjustStockAsync(new(Guid.Empty, f.P1, 0), default));
        Invalid(() => f.Service.AdjustStockAsync(new(f.WarehouseId, Guid.Empty, 0), default));
        foreach (var quantity in new decimal?[] { null, -1, 0.0001m, 1000000000000000m })
            Invalid(() => f.Service.AdjustStockAsync(new(f.WarehouseId, f.P1, quantity), default));
        f.Store.VerifyNoOtherCalls();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Adjustment_sets_an_absolute_count_for_existing_or_missing_pairs(bool missing)
    {
        var id = missing ? f.P2 : f.P1;
        var result = await f.Service.AdjustStockAsync(new(f.WarehouseId, id, 3.125m), default);
        Assert.That(result.PreviousQuantity, Is.EqualTo(missing ? 0 : 10));
        Assert.That(result.Quantity, Is.EqualTo(3.125m));
        Assert.That(f.Balances[id].Quantity, Is.EqualTo(3.125m));
        f.Transaction.Verify(t => t.CommitAsync(default), Times.Once);
    }

    [Test]
    public void Failed_commit_is_propagated_and_transaction_is_disposed()
    {
        var failure = new InvalidOperationException("commit failed");
        f.Transaction.Setup(t => t.CommitAsync(default)).ThrowsAsync(failure);
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Service.CreateInboundAsync(new("IN-1", f.WarehouseId, f.Date, [new(f.P1, 2)]), f.ActorId, default)), Is.SameAs(failure));
        f.Transaction.Verify(t => t.DisposeAsync(), Times.Once);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public async Task Blank_optional_descriptions_are_persisted_as_null(string? description)
    {
        f.Store.Setup(s => s.AddProductAsync(It.Is<Product>(p => p.Description == null), default)).Returns(Task.CompletedTask);
        Assert.That((await f.Service.CreateProductAsync(new("SKU", "Product", description), default)).Description, Is.Null);
    }

    [TestCase("bad\r")]
    [TestCase("bad\n")]
    [TestCase("bad\t")]
    public async Task Optional_multiline_text_accepts_line_breaks_and_tabs(string description)
    {
        f.Store.Setup(s => s.AddProductAsync(It.IsAny<Product>(), default)).Returns(Task.CompletedTask);
        Assert.That((await f.Service.CreateProductAsync(new("SKU", "Product", description + "x"), default)).Description, Is.EqualTo(description + "x"));
    }

    private static void Invalid(Func<Task> action) => Error(action, BusinessErrorKind.Validation);
    private static void Error(Func<Task> action, BusinessErrorKind kind) =>
        Assert.That(Assert.ThrowsAsync<BusinessException>(async () => await action())?.Kind, Is.EqualTo(kind));

    private sealed class Fixture
    {
        public Guid P1 { get; } = Guid.NewGuid();
        public Guid P2 { get; } = Guid.NewGuid();
        public Guid WarehouseId { get; } = Guid.NewGuid();
        public Guid ActorId { get; } = Guid.NewGuid();
        public DateTimeOffset Date { get; } = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        public Mock<IWarehouseStore> Store { get; } = new(MockBehavior.Strict);
        public Mock<IStaffPasswordHasher> Passwords { get; } = new(MockBehavior.Strict);
        public Mock<IStockTransaction> Transaction { get; } = new(MockBehavior.Strict);
        public Dictionary<Guid, Product> Products { get; } = [];
        public Dictionary<Guid, StockBalance> Balances { get; } = [];
        public Warehouse? Warehouse { get; set; }
        public InboundReceipt? Inbound { get; private set; }
        public OutboundReceipt? Outbound { get; private set; }
        public WarehouseService Service { get; }
        public Fixture()
        {
            Warehouse = new Warehouse { WarehouseId = WarehouseId, Code = "WH-1", Name = "Warehouse", Address = "Address" };
            Products.Add(P1, new Product { ProductId = P1, SKU = "SKU-1", Name = "Product" });
            Products.Add(P2, new Product { ProductId = P2, SKU = "SKU-2", Name = "Product 2" });
            Balances.Add(P1, new StockBalance { WarehouseId = WarehouseId, ProductId = P1, Quantity = 10 });
            Store.Setup(s => s.BeginStockChangeAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            Transaction.SetupGet(t => t.Warehouse).Returns(() => Warehouse);
            Transaction.SetupGet(t => t.Products).Returns(Products);
            Transaction.SetupGet(t => t.Balances).Returns(Balances);
            Transaction.Setup(t => t.AddBalance(It.IsAny<StockBalance>())).Callback<StockBalance>(b => Balances.Add(b.ProductId, b));
            Transaction.Setup(t => t.AddInbound(It.IsAny<InboundReceipt>())).Callback<InboundReceipt>(r => Inbound = r);
            Transaction.Setup(t => t.AddOutbound(It.IsAny<OutboundReceipt>())).Callback<OutboundReceipt>(r => Outbound = r);
            Transaction.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);
            Service = new WarehouseService(Store.Object, Passwords.Object);
        }
    }
}
