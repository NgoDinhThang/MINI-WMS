using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MiniWms.Application.Authentication;
using MiniWms.Application.Business;
using MiniWms.Domain.Entities;
using NUnit.Framework;

namespace MiniWms.Api.IntegrationTests;

public sealed partial class AuthApiIntegrationTests
{
    [Test]
    public async Task Admin_catalog_creation_and_Staff_receipt_issue_flow_persist_documents_and_exact_stock()
    {
        var admin = await LoginAsync("admin@integration.test");
        using var productResponse = await BusinessRequest(Client, admin.AccessToken, "/products",
            new { sku = " test-sku ", name = " Test product ", description = "Description" });
        Assert.That(productResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var product = await Read<ProductDto>(productResponse);
        using var warehouseResponse = await BusinessRequest(Client, admin.AccessToken, "/warehouses",
            new { code = " test-wh ", name = "Warehouse", address = "Address" });
        Assert.That(warehouseResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var warehouse = await Read<WarehouseDto>(warehouseResponse);
        var staff = await LoginAsync();
        var date = DateTimeOffset.UtcNow;
        using var inbound = await BusinessRequest(Client, staff.AccessToken, "/inbound-receipts", new
        {
            receiptCode = " in-test ", warehouseId = warehouse.WarehouseId, receivedAt = date,
            userId = adminId, note = "Received goods", lines = new[] { new { productId = product.ProductId, quantity = 10.500m } }
        });
        Assert.That(inbound.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var received = await Read<ReceiptDto>(inbound);
        using var outbound = await BusinessRequest(Client, staff.AccessToken, "/outbound-receipts", new
        {
            receiptCode = " out-test ", warehouseId = warehouse.WarehouseId, recipientName = "Customer",
            issuedAt = date, note = "Issued goods", userId = adminId,
            lines = new[] { new { productId = product.ProductId, quantity = 3.250m } }
        });
        Assert.That(outbound.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var issued = await Read<ReceiptDto>(outbound);
        await using var context = CreateContext();
        var persistedInbound = await context.InboundReceipts.Include(r => r.Details).SingleAsync();
        var persistedOutbound = await context.OutboundReceipts.Include(r => r.Details).SingleAsync();
        var balance = await context.StockBalances.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(product.SKU, Is.EqualTo("TEST-SKU"));
            Assert.That(warehouse.Code, Is.EqualTo("TEST-WH"));
            Assert.That(received.UserId, Is.EqualTo(staffId));
            Assert.That(issued.UserId, Is.EqualTo(staffId));
            Assert.That(persistedInbound.ReceiptCode, Is.EqualTo("IN-TEST"));
            Assert.That(persistedInbound.Details.Single().Quantity, Is.EqualTo(10.500m));
            Assert.That(persistedOutbound.ReceiptCode, Is.EqualTo("OUT-TEST"));
            Assert.That(persistedOutbound.RecipientName, Is.EqualTo("Customer"));
            Assert.That(persistedOutbound.Note, Is.EqualTo("Issued goods"));
            Assert.That(persistedOutbound.UserId, Is.EqualTo(staffId));
            Assert.That(persistedOutbound.Details.Single().Quantity, Is.EqualTo(3.250m));
            Assert.That(balance.Quantity, Is.EqualTo(7.250m));
            Assert.That(balance.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(balance.UpdatedAt, Is.GreaterThanOrEqualTo(balance.CreatedAt));
        });
        using var lookup = await BusinessRequest(Client, staff.AccessToken,
            $"/inventory?productId={product.ProductId}&warehouseId={warehouse.WarehouseId}", method: HttpMethod.Get);
        Assert.That(lookup.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Read<PageResult<StockDto>>(lookup)).Items.Single().Quantity, Is.EqualTo(7.250m));
    }

    [Test]
    public async Task A_failed_second_issue_line_rolls_back_the_whole_document_and_all_stock()
    {
        var (warehouse, first) = await SeedStock(10);
        var second = new Product { SKU = "SECOND", Name = "Second" };
        await using (var context = CreateContext())
        {
            context.Products.Add(second);
            context.StockBalances.Add(new StockBalance { WarehouseId = warehouse.WarehouseId, ProductId = second.ProductId, Quantity = 1 });
            await context.SaveChangesAsync();
        }
        var staff = await LoginAsync();
        using var response = await BusinessRequest(Client, staff.AccessToken, "/outbound-receipts", new
        {
            receiptCode = "OUT-INVALID", warehouseId = warehouse.WarehouseId, recipientName = "Customer", issuedAt = DateTimeOffset.UtcNow,
            lines = new[] { new { productId = first.ProductId, quantity = 2 }, new { productId = second.ProductId, quantity = 3 } }
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        await using var check = CreateContext();
        Assert.That(await check.OutboundReceipts.CountAsync(), Is.Zero);
        Assert.That(await check.OutboundReceiptDetails.CountAsync(), Is.Zero);
        Assert.That((await check.StockBalances.SingleAsync(b => b.ProductId == first.ProductId)).Quantity, Is.EqualTo(10));
        Assert.That((await check.StockBalances.SingleAsync(b => b.ProductId == second.ProductId)).Quantity, Is.EqualTo(1));
    }

    [TestCase("duplicate")]
    [TestCase("precision")]
    [TestCase("zero")]
    [TestCase("null")]
    public async Task Invalid_lines_are_rejected_before_any_document_is_written(string invalid)
    {
        var (warehouse, product) = await SeedStock(10);
        var staff = await LoginAsync();
        object?[] lines = invalid switch
        {
            "duplicate" => [new { productId = product.ProductId, quantity = 1m }, new { productId = product.ProductId, quantity = 1m }],
            "precision" => [new { productId = product.ProductId, quantity = 0.0001m }],
            "zero" => [new { productId = product.ProductId, quantity = 0m }],
            _ => [null]
        };
        using var response = await BusinessRequest(Client, staff.AccessToken, "/inbound-receipts", new
        {
            receiptCode = "IN-BAD", warehouseId = warehouse.WarehouseId, receivedAt = DateTimeOffset.UtcNow, lines
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await using var context = CreateContext();
        Assert.That(await context.InboundReceipts.CountAsync(), Is.Zero);
        Assert.That((await context.StockBalances.SingleAsync()).Quantity, Is.EqualTo(10));
    }

    [TestCase("product", HttpStatusCode.Conflict)]
    [TestCase("warehouse", HttpStatusCode.Conflict)]
    [TestCase("missing", HttpStatusCode.NotFound)]
    public async Task Invalid_references_reject_the_entire_receipt(string invalid, HttpStatusCode expected)
    {
        var (warehouse, product) = await SeedStock(10);
        await using (var context = CreateContext())
        {
            if (invalid == "product") { (await context.Products.SingleAsync()).IsActive = false; }
            if (invalid == "warehouse") { (await context.Warehouses.SingleAsync()).IsActive = false; }
            await context.SaveChangesAsync();
        }
        var staff = await LoginAsync();
        using var response = await BusinessRequest(Client, staff.AccessToken, "/inbound-receipts", new
        {
            receiptCode = "IN-BAD", warehouseId = warehouse.WarehouseId, receivedAt = DateTimeOffset.UtcNow,
            lines = new[] { new { productId = invalid == "missing" ? Guid.NewGuid() : product.ProductId, quantity = 1 } }
        });
        Assert.That(response.StatusCode, Is.EqualTo(expected));
        await using var check = CreateContext();
        Assert.That(await check.InboundReceipts.CountAsync(), Is.Zero);
        Assert.That((await check.StockBalances.SingleAsync()).Quantity, Is.EqualTo(10));
    }

    [Test]
    public async Task Concurrent_first_receipts_across_hosts_create_one_balance_without_lost_updates()
    {
        var (warehouse, product) = await SeedStock(null);
        var staff = await LoginAsync();
        await using var secondFactory = new AuthApiFactory(configuration);
        using var secondClient = secondFactory.CreateClient();
        var requests = Enumerable.Range(0, 12).Select(i => BusinessRequest(i % 2 == 0 ? Client : secondClient,
            staff.AccessToken, "/inbound-receipts", new
            {
                receiptCode = "IN-" + i, warehouseId = warehouse.WarehouseId, receivedAt = DateTimeOffset.UtcNow,
                lines = new[] { new { productId = product.ProductId, quantity = 1.125m } }
            })).ToArray();
        var responses = await Task.WhenAll(requests);
        try { Assert.That(responses.All(r => r.StatusCode == HttpStatusCode.Created), Is.True); }
        finally { foreach (var response in responses) { response.Dispose(); } }
        await using var context = CreateContext();
        Assert.That(await context.StockBalances.CountAsync(), Is.EqualTo(1));
        Assert.That((await context.StockBalances.SingleAsync()).Quantity, Is.EqualTo(13.5m));
        Assert.That(await context.InboundReceipts.CountAsync(), Is.EqualTo(12));
        Assert.That(await context.InboundReceiptDetails.SumAsync(l => l.Quantity), Is.EqualTo(13.5m));
    }

    [Test]
    public async Task Concurrent_issues_across_hosts_never_oversell()
    {
        var (warehouse, product) = await SeedStock(10);
        var staff = await LoginAsync();
        await using var secondFactory = new AuthApiFactory(configuration);
        using var secondClient = secondFactory.CreateClient();
        var requests = Enumerable.Range(0, 8).Select(i => BusinessRequest(i % 2 == 0 ? Client : secondClient,
            staff.AccessToken, "/outbound-receipts", new
            {
                receiptCode = "OUT-" + i, warehouseId = warehouse.WarehouseId, recipientName = "Customer",
                issuedAt = DateTimeOffset.UtcNow, lines = new[] { new { productId = product.ProductId, quantity = 3 } }
            })).ToArray();
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Created), Is.EqualTo(3));
            Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(5));
        }
        finally { foreach (var response in responses) { response.Dispose(); } }
        await using var context = CreateContext();
        Assert.That((await context.StockBalances.SingleAsync()).Quantity, Is.EqualTo(1));
        Assert.That(await context.OutboundReceipts.CountAsync(), Is.EqualTo(3));
        Assert.That(await context.OutboundReceiptDetails.SumAsync(l => l.Quantity), Is.EqualTo(9));
    }

    [Test]
    public async Task Concurrent_adjustment_and_receipt_have_a_serial_result()
    {
        var (warehouse, product) = await SeedStock(10);
        var admin = await LoginAsync("admin@integration.test");
        var staff = await LoginAsync();
        using var receive = await BusinessRequest(Client, staff.AccessToken, "/inbound-receipts", new
        {
            receiptCode = "INITIAL", warehouseId = warehouse.WarehouseId, receivedAt = DateTimeOffset.UtcNow,
            lines = new[] { new { productId = product.ProductId, quantity = 1 } }
        });
        Assert.That(receive.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var results = await Task.WhenAll(
            BusinessRequest(Client, admin.AccessToken, "/inventory/adjustments",
                new { warehouseId = warehouse.WarehouseId, productId = product.ProductId, quantity = 5 }),
            BusinessRequest(Client, staff.AccessToken, "/inbound-receipts", new
            {
                receiptCode = "RACE", warehouseId = warehouse.WarehouseId, receivedAt = DateTimeOffset.UtcNow,
                lines = new[] { new { productId = product.ProductId, quantity = 2 } }
            }));
        try
        {
            Assert.That(results[0].StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(results[1].StatusCode, Is.EqualTo(HttpStatusCode.Created));
            var adjustment = await Read<AdjustmentDto>(results[0]);
            await using var context = CreateContext();
            var quantity = (await context.StockBalances.SingleAsync()).Quantity;
            Assert.That((adjustment.PreviousQuantity, quantity), Is.AnyOf((11m, 7m), (13m, 5m)));
        }
        finally { foreach (var response in results) { response.Dispose(); } }
    }

    [Test]
    public async Task Duplicate_codes_and_document_codes_return_conflict_without_changing_stock()
    {
        var (warehouse, product) = await SeedStock(10);
        var admin = await LoginAsync("admin@integration.test");
        using var duplicateProduct = await BusinessRequest(Client, admin.AccessToken, "/products",
            new { sku = " " + product.SKU.ToLowerInvariant() + " ", name = "Duplicate" });
        using var duplicateWarehouse = await BusinessRequest(Client, admin.AccessToken, "/warehouses",
            new { code = warehouse.Code.ToLowerInvariant(), name = "Duplicate", address = "Address" });
        Assert.That(duplicateProduct.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(duplicateWarehouse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var body = new
        {
            receiptCode = "IN-DUP", warehouseId = warehouse.WarehouseId, receivedAt = DateTimeOffset.UtcNow,
            lines = new[] { new { productId = product.ProductId, quantity = 2 } }
        };
        using var first = await BusinessRequest(Client, admin.AccessToken, "/inbound-receipts", body);
        using var duplicate = await BusinessRequest(Client, admin.AccessToken, "/inbound-receipts", body);
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(duplicate.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        await using var context = CreateContext();
        Assert.That((await context.StockBalances.SingleAsync()).Quantity, Is.EqualTo(12));
        Assert.That(await context.InboundReceipts.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Lookup_supports_filters_pagination_and_zero_for_an_unrecorded_exact_pair()
    {
        var (warehouse, product) = await SeedStock(null);
        var staff = await LoginAsync();
        using var products = await BusinessRequest(Client, staff.AccessToken, "/products?search=widget&isActive=true&pageSize=1", method: HttpMethod.Get);
        Assert.That(products.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Read<PageResult<ProductDto>>(products)).Items.Single().ProductId, Is.EqualTo(product.ProductId));
        using var warehouses = await BusinessRequest(Client, staff.AccessToken, "/warehouses?search=warehouse&isActive=true", method: HttpMethod.Get);
        Assert.That(warehouses.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Read<PageResult<WarehouseDto>>(warehouses)).Items.Single().WarehouseId, Is.EqualTo(warehouse.WarehouseId));
        using var stock = await BusinessRequest(Client, staff.AccessToken,
            $"/inventory?productId={product.ProductId}&warehouseId={warehouse.WarehouseId}", method: HttpMethod.Get);
        Assert.That(stock.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Read<PageResult<StockDto>>(stock)).Items.Single().Quantity, Is.Zero);
        using var missing = await BusinessRequest(Client, staff.AccessToken, $"/inventory?productId={Guid.NewGuid()}", method: HttpMethod.Get);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using var badPage = await BusinessRequest(Client, staff.AccessToken, "/products?pageSize=201", method: HttpMethod.Get);
        Assert.That(badPage.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Staff_creation_hashes_the_password_fixes_the_role_and_supports_ban_unban()
    {
        var admin = await LoginAsync("admin@integration.test");
        using var create = await BusinessRequest(Client, admin.AccessToken, "/staff",
            new { email = " NEW@INTEGRATION.TEST ", fullName = "New Staff", password, role = "Admin" });
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var staff = await Read<StaffDto>(create);
        var responseText = await create.Content.ReadAsStringAsync();
        Assert.That(responseText.Contains("password", StringComparison.OrdinalIgnoreCase), Is.False);
        Assert.That(staff.Role, Is.EqualTo("Staff"));
        await using (var context = CreateContext())
        {
            var user = await context.Users.Include(u => u.Role).SingleAsync(u => u.UserId == staff.UserId);
            Assert.That(user.Role?.Name, Is.EqualTo("Staff"));
            Assert.That(new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, password),
                Is.Not.EqualTo(PasswordVerificationResult.Failed));
        }
        try
        {
            var credentials = await LoginAsync(staff.Email);
            using var banned = await ChangeBanAsync(Client, admin.AccessToken, staff.UserId, true);
            Assert.That(banned.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            using var denied = await BusinessRequest(Client, credentials.AccessToken, "/products", method: HttpMethod.Get);
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            using var unbanned = await ChangeBanAsync(Client, admin.AccessToken, staff.UserId, false);
            Assert.That(unbanned.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            using var oldToken = await MeAsync(Client, credentials.AccessToken);
            Assert.That(oldToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            var fresh = await LoginAsync(staff.Email);
            using var me = await MeAsync(Client, fresh.AccessToken);
            Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally { await Redis.KeyDeleteAsync(SessionKey(staff.UserId)); }
        using var duplicate = await BusinessRequest(Client, admin.AccessToken, "/staff",
            new { email = staff.Email.ToUpperInvariant(), fullName = "Duplicate", password });
        Assert.That(duplicate.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task All_business_endpoints_require_authentication_and_Staff_cannot_use_Admin_operations()
    {
        var staff = await LoginAsync();
        foreach (var path in new[] { "/products", "/warehouses", "/inventory/adjustments", "/staff" })
        {
            using var response = await BusinessRequest(Client, staff.AccessToken, path, new { });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), path);
        }
        foreach (var path in new[] { "/products", "/warehouses", "/inventory", "/inbound-receipts", "/outbound-receipts", "/inventory/adjustments", "/staff" })
        {
            var method = path is "/products" or "/warehouses" or "/inventory" ? HttpMethod.Get : HttpMethod.Post;
            using var response = await BusinessRequest(Client, null, path, new { }, method);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), path);
        }
        using var banned = await LoginAndBanStaff();
        using var afterBan = await BusinessRequest(Client, staff.AccessToken, "/inbound-receipts", new { });
        Assert.That(afterBan.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        async Task<HttpResponseMessage> LoginAndBanStaff()
        {
            var admin = await LoginAsync("admin@integration.test");
            return await ChangeBanAsync(Client, admin.AccessToken, staffId, true);
        }
    }

    [Test]
    public async Task Swagger_documents_every_business_route_with_bearer_security()
    {
        using var response = await Client.GetAsync("/swagger/v1/swagger.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        foreach (var (path, method) in new[]
        {
            ("/products", "get"), ("/products", "post"), ("/warehouses", "get"), ("/warehouses", "post"),
            ("/inventory", "get"), ("/inbound-receipts", "post"), ("/outbound-receipts", "post"),
            ("/inventory/adjustments", "post"), ("/staff", "post")
        })
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            Assert.That(operation.GetProperty("security").GetArrayLength(), Is.GreaterThan(0));
        }
    }


    [Test]
    public async Task Admin_can_create_a_zero_balance_and_set_fractional_counts_but_invalid_counts_do_not_change_it()
    {
        var (warehouse, product) = await SeedStock(null);
        var admin = await LoginAsync("admin@integration.test");
        using var first = await BusinessRequest(Client, admin.AccessToken, "/inventory/adjustments",
            new { warehouseId = warehouse.WarehouseId, productId = product.ProductId, quantity = 0 });
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Read<AdjustmentDto>(first)).PreviousQuantity, Is.Zero);
        using var next = await BusinessRequest(Client, admin.AccessToken, "/inventory/adjustments",
            new { warehouseId = warehouse.WarehouseId, productId = product.ProductId, quantity = 3.125m });
        Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        foreach (var invalid in new decimal?[] { null, -0.001m, 0.0001m, 1000000000000000m })
        {
            using var rejected = await BusinessRequest(Client, admin.AccessToken, "/inventory/adjustments",
                new { warehouseId = warehouse.WarehouseId, productId = product.ProductId, quantity = invalid });
            Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }
        await using var context = CreateContext();
        Assert.That((await context.StockBalances.SingleAsync()).Quantity, Is.EqualTo(3.125m));
        Assert.That(await context.InboundReceipts.CountAsync(), Is.Zero);
        Assert.That(await context.OutboundReceipts.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Overflow_on_a_later_inbound_line_rolls_back_every_balance()
    {
        var (warehouse, product) = await SeedStock(10);
        var full = new Product { SKU = "FULL", Name = "Full capacity" };
        await using (var context = CreateContext())
        {
            context.Products.Add(full);
            context.StockBalances.Add(new StockBalance
            {
                WarehouseId = warehouse.WarehouseId, ProductId = full.ProductId,
                Quantity = MiniWms.Domain.Inventory.StockRules.MaximumQuantity
            });
            await context.SaveChangesAsync();
        }
        var staff = await LoginAsync();
        using var response = await BusinessRequest(Client, staff.AccessToken, "/inbound-receipts", new
        {
            receiptCode = "OVERFLOW", warehouseId = warehouse.WarehouseId, receivedAt = DateTimeOffset.UtcNow,
            lines = new[] { new { productId = product.ProductId, quantity = 1m }, new { productId = full.ProductId, quantity = 0.001m } }
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        await using var check = CreateContext();
        Assert.That((await check.StockBalances.SingleAsync(b => b.ProductId == product.ProductId)).Quantity, Is.EqualTo(10));
        Assert.That((await check.StockBalances.SingleAsync(b => b.ProductId == full.ProductId)).Quantity,
            Is.EqualTo(MiniWms.Domain.Inventory.StockRules.MaximumQuantity));
        Assert.That(await check.InboundReceipts.CountAsync(), Is.Zero);
        Assert.That(await check.InboundReceiptDetails.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Concurrent_duplicate_receipt_codes_in_different_warehouses_commit_stock_only_once()
    {
        var (warehouse, product) = await SeedStock(null);
        var other = new Warehouse { Code = "OTHER", Name = "Other warehouse", Address = "Address" };
        await using (var context = CreateContext())
        {
            context.Warehouses.Add(other);
            await context.SaveChangesAsync();
        }
        var staff = await LoginAsync();
        var responses = await Task.WhenAll(new[] { warehouse.WarehouseId, other.WarehouseId }.Select(id =>
            BusinessRequest(Client, staff.AccessToken, "/inbound-receipts", new
            {
                receiptCode = "SAME-CODE", warehouseId = id, receivedAt = DateTimeOffset.UtcNow,
                lines = new[] { new { productId = product.ProductId, quantity = 2 } }
            })));
        try
        {
            Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Created), Is.EqualTo(1));
            Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(1));
        }
        finally { foreach (var response in responses) { response.Dispose(); } }
        await using var check = CreateContext();
        Assert.That(await check.InboundReceipts.CountAsync(), Is.EqualTo(1));
        Assert.That(await check.StockBalances.CountAsync(), Is.EqualTo(1));
        Assert.That(await check.StockBalances.SumAsync(b => b.Quantity), Is.EqualTo(2));
    }

    private async Task<(Warehouse Warehouse, Product Product)> SeedStock(decimal? quantity)
    {
        await using var context = CreateContext();
        var warehouse = new Warehouse { Code = "WH-" + Guid.NewGuid().ToString("N"), Name = "Warehouse", Address = "Address" };
        var product = new Product { SKU = "SKU-" + Guid.NewGuid().ToString("N"), Name = "Widget" };
        context.Warehouses.Add(warehouse);
        context.Products.Add(product);
        if (quantity.HasValue)
        {
            context.StockBalances.Add(new StockBalance { WarehouseId = warehouse.WarehouseId, ProductId = product.ProductId, Quantity = quantity.Value });
        }
        await context.SaveChangesAsync();
        return (warehouse, product);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException("Missing business response.");

    private static async Task<HttpResponseMessage> BusinessRequest(
        HttpClient target, string? accessToken, string path, object? body = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path);
        if (accessToken is not null) { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); }
        if (body is not null && request.Method != HttpMethod.Get) { request.Content = JsonContent.Create(body); }
        return await target.SendAsync(request);
    }
}
