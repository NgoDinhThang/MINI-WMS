using MiniWms.Domain.Entities;
using MiniWms.Domain.Inventory;
using MiniWms.Infrastructure.Persistence.Seeding;
using Microsoft.AspNetCore.Identity;
using Moq;
using NUnit.Framework;

namespace MiniWms.Infrastructure.Tests;

[TestFixture]
[Category("Unit")]
public sealed class DomainGraphAndFixtureTests
{
    [Test]
    public void The_complete_navigation_graph_retains_matching_foreign_keys_and_relationships()
    {
        var role = new Role { Name = "Staff" };
        var user = new User { Role = role, RoleId = role.RoleId };
        var warehouse = new Warehouse();
        var product = new Product();
        var inbound = new InboundReceipt { User = user, UserId = user.UserId, Warehouse = warehouse, WarehouseId = warehouse.WarehouseId };
        var received = new InboundReceiptDetail { InboundReceipt = inbound, InboundReceiptId = inbound.InboundReceiptId, Product = product, ProductId = product.ProductId };
        var outbound = new OutboundReceipt { User = user, UserId = user.UserId, Warehouse = warehouse, WarehouseId = warehouse.WarehouseId };
        var issued = new OutboundReceiptDetail { OutboundReceipt = outbound, OutboundReceiptId = outbound.OutboundReceiptId, Product = product, ProductId = product.ProductId };
        var balance = new StockBalance { Warehouse = warehouse, WarehouseId = warehouse.WarehouseId, Product = product, ProductId = product.ProductId };
        var session = new AuthSession { User = user, UserId = user.UserId };
        var refresh = new RefreshToken { Session = session, SessionId = session.SessionId };
        role.Users.Add(user);
        user.AuthSessions.Add(session);
        user.InboundReceipts.Add(inbound);
        user.OutboundReceipts.Add(outbound);
        warehouse.StockBalances.Add(balance);
        warehouse.InboundReceipts.Add(inbound);
        warehouse.OutboundReceipts.Add(outbound);
        product.StockBalances.Add(balance);
        product.InboundReceiptDetails.Add(received);
        product.OutboundReceiptDetails.Add(issued);
        inbound.Details.Add(received);
        outbound.Details.Add(issued);
        session.RefreshTokens.Add(refresh);
        Assert.Multiple(() =>
        {
            Assert.That(inbound.User?.UserId, Is.EqualTo(inbound.UserId));
            Assert.That(inbound.Warehouse?.WarehouseId, Is.EqualTo(inbound.WarehouseId));
            Assert.That(outbound.User?.UserId, Is.EqualTo(outbound.UserId));
            Assert.That(outbound.Warehouse?.WarehouseId, Is.EqualTo(outbound.WarehouseId));
            Assert.That(received.InboundReceipt?.InboundReceiptId, Is.EqualTo(received.InboundReceiptId));
            Assert.That(received.Product?.ProductId, Is.EqualTo(received.ProductId));
            Assert.That(issued.OutboundReceipt?.OutboundReceiptId, Is.EqualTo(issued.OutboundReceiptId));
            Assert.That(issued.Product?.ProductId, Is.EqualTo(issued.ProductId));
            Assert.That(balance.Warehouse?.WarehouseId, Is.EqualTo(balance.WarehouseId));
            Assert.That(balance.Product?.ProductId, Is.EqualTo(balance.ProductId));
            Assert.That(session.User?.AuthSessions.Single(), Is.SameAs(session));
            Assert.That(refresh.Session?.RefreshTokens.Single(), Is.SameAs(refresh));
            Assert.That(role.Users.Single(), Is.SameAs(user));
            Assert.That(user.InboundReceipts.Single().Details.Single(), Is.SameAs(received));
            Assert.That(user.OutboundReceipts.Single().Details.Single(), Is.SameAs(issued));
            Assert.That(product.StockBalances.Single(), Is.SameAs(warehouse.StockBalances.Single()));
        });
    }

    [Test]
    public void Published_quantity_capacity_matches_the_supported_numeric_boundary()
    {
        var field = typeof(StockRules).GetField(nameof(StockRules.MaximumQuantity)) ?? throw new InvalidOperationException();
        Assert.That(field.GetValue(null), Is.EqualTo(999999999999999.999m));
        Assert.That(StockRules.IsValidQuantity(StockRules.MaximumQuantity, true), Is.True);
    }

    [Test]
    public void Fixture_rejects_duplicate_existing_roles_instead_of_selecting_one_arbitrarily()
    {
        var hasher = new Mock<IPasswordHasher<User>>();
        Assert.Throws<InvalidOperationException>(() => MockDataFixtures.Create(
            new MockDataOptions { AdminPassword = "unit-admin-password", StaffPassword = "unit-staff-password" },
            hasher.Object, [new Role { Name = "Admin" }, new Role { Name = "Admin" }]));
    }
}
