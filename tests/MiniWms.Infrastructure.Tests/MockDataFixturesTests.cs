using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Persistence.Seeding;
using Microsoft.AspNetCore.Identity;
using NUnit.Framework;

namespace MiniWms.Infrastructure.Tests;

[TestFixture]
public sealed class MockDataFixturesTests
{
    private static readonly MockDataOptions TestPasswords = new()
    {
        AdminPassword = "Fixture-only-Admin-Password!",
        StaffPassword = "Fixture-only-Staff-Password!"
    };

    private readonly PasswordHasher<User> _passwordHasher = new();
    private MockDataSet? _fixture;

    private MockDataSet Fixture => _fixture ?? throw new InvalidOperationException("Fixture setup has not run.");

    [OneTimeSetUp]
    public void CreateFixture() => _fixture = MockDataFixtures.Create(TestPasswords, _passwordHasher);

    [Test]
    public void Every_stock_balance_matches_the_flat_receipt_ledger_with_valid_quantities()
    {
        var inbounds = Fixture.InboundReceipts.ToDictionary(receipt => receipt.InboundReceiptId);
        var outbounds = Fixture.OutboundReceipts.ToDictionary(receipt => receipt.OutboundReceiptId);
        var movements = Fixture.InboundReceiptDetails.Select(line => (
                Pair: (inbounds[line.InboundReceiptId].WarehouseId, line.ProductId), Quantity: line.Quantity))
            .Concat(Fixture.OutboundReceiptDetails.Select(line => (
                Pair: (outbounds[line.OutboundReceiptId].WarehouseId, line.ProductId), Quantity: -line.Quantity)))
            .GroupBy(movement => movement.Pair)
            .ToDictionary(group => group.Key, group => group.Sum(movement => movement.Quantity));
        var stock = Fixture.StockBalances.ToDictionary(balance => (balance.WarehouseId, balance.ProductId));

        Assert.That(stock.Keys, Is.EquivalentTo(movements.Keys));
        foreach (var (pair, quantity) in movements)
        {
            Assert.Multiple(() =>
            {
                Assert.That(stock[pair].Quantity, Is.EqualTo(quantity), pair.ToString());
                Assert.That(quantity, Is.GreaterThanOrEqualTo(0m));
                Assert.That(decimal.Round(quantity, 3), Is.EqualTo(quantity));
            });
        }

        var lineQuantities = Fixture.InboundReceiptDetails.Select(line => line.Quantity)
            .Concat(Fixture.OutboundReceiptDetails.Select(line => line.Quantity));
        Assert.That(lineQuantities.All(quantity => quantity > 0m && decimal.Round(quantity, 3) == quantity), Is.True);
        Assert.That(stock.Values.Any(balance => balance.Quantity == 0m), Is.True);
        Assert.That(stock.Values.Any(balance => decimal.Truncate(balance.Quantity) != balance.Quantity), Is.True);
    }

    [Test]
    public void Receipt_fixtures_have_valid_active_references_and_one_line_per_product()
    {
        var products = Fixture.Products.ToDictionary(product => product.ProductId);
        var warehouses = Fixture.Warehouses.ToDictionary(warehouse => warehouse.WarehouseId);
        var users = Fixture.Users.ToDictionary(user => user.UserId);

        foreach (var receipt in Fixture.InboundReceipts)
        {
            Assert.Multiple(() =>
            {
                Assert.That(warehouses[receipt.WarehouseId].IsActive, Is.True);
                Assert.That(users[receipt.UserId].IsActive, Is.True);
                Assert.That(receipt.ReceivedAt.Offset, Is.EqualTo(TimeSpan.Zero));
                Assert.That(receipt.Details.Select(line => line.InboundDetailId),
                    Is.EquivalentTo(Fixture.InboundReceiptDetails
                        .Where(line => line.InboundReceiptId == receipt.InboundReceiptId)
                        .Select(line => line.InboundDetailId)));
                Assert.That(receipt.Details.All(line => products[line.ProductId].IsActive), Is.True);
                Assert.That(receipt.Details.Select(line => line.ProductId).Distinct().Count(), Is.EqualTo(receipt.Details.Count));
            });
        }

        foreach (var receipt in Fixture.OutboundReceipts)
        {
            Assert.Multiple(() =>
            {
                Assert.That(warehouses[receipt.WarehouseId].IsActive, Is.True);
                Assert.That(users[receipt.UserId].IsActive, Is.True);
                Assert.That(receipt.IssuedAt.Offset, Is.EqualTo(TimeSpan.Zero));
                Assert.That(receipt.RecipientName, Is.Not.Empty);
                Assert.That(receipt.Details.Select(line => line.OutboundDetailId),
                    Is.EquivalentTo(Fixture.OutboundReceiptDetails
                        .Where(line => line.OutboundReceiptId == receipt.OutboundReceiptId)
                        .Select(line => line.OutboundDetailId)));
                Assert.That(receipt.Details.All(line => products[line.ProductId].IsActive), Is.True);
                Assert.That(receipt.Details.Select(line => line.ProductId).Distinct().Count(), Is.EqualTo(receipt.Details.Count));
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(Fixture.InboundReceiptDetails.All(line =>
                Fixture.InboundReceipts.Any(receipt => receipt.InboundReceiptId == line.InboundReceiptId)), Is.True);
            Assert.That(Fixture.OutboundReceiptDetails.All(line =>
                Fixture.OutboundReceipts.Any(receipt => receipt.OutboundReceiptId == line.OutboundReceiptId)), Is.True);
            Assert.That(Fixture.StockBalances.All(balance =>
                products.ContainsKey(balance.ProductId) && warehouses.ContainsKey(balance.WarehouseId)), Is.True);
            Assert.That(Fixture.Products.Any(product => !product.IsActive), Is.True);
            Assert.That(Fixture.Warehouses.Any(warehouse => !warehouse.IsActive), Is.True);
        });
    }

    [Test]
    public void Exactly_one_Admin_and_the_Staff_accounts_use_verifiable_nonplaintext_password_hashes()
    {
        var roles = Fixture.Roles.ToDictionary(role => role.RoleId);
        var admin = Fixture.Users.Single(user => roles[user.RoleId].Name == "Admin");
        var staff = Fixture.Users.Where(user => roles[user.RoleId].Name == "Staff").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(admin.IsActive, Is.True);
            Assert.That(staff.Count(user => user.IsActive), Is.EqualTo(2));
            Assert.That(staff.Count(user => !user.IsActive), Is.EqualTo(1));
            Assert.That(Fixture.Users.Count, Is.EqualTo(staff.Length + 1));
            Assert.That(Fixture.Users.Select(user => user.Email).Distinct().Count(), Is.EqualTo(Fixture.Users.Count));
        });

        foreach (var user in Fixture.Users)
        {
            var password = user.UserId == admin.UserId ? TestPasswords.AdminPassword : TestPasswords.StaffPassword;
            Assert.Multiple(() =>
            {
                Assert.That(user.PasswordHash, Is.Not.EqualTo(password));
                Assert.That(_passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password),
                    Is.Not.EqualTo(PasswordVerificationResult.Failed));
                Assert.That(_passwordHasher.VerifyHashedPassword(user, user.PasswordHash, "Incorrect-fixture-password!"),
                    Is.EqualTo(PasswordVerificationResult.Failed));
            });
        }

        Assert.That(staff.Select(user => user.PasswordHash).Distinct().Count(), Is.EqualTo(staff.Length),
            "Identical Staff passwords must still receive independently salted hashes.");
    }

    [Test]
    public void Fixture_keys_are_reproducible_and_existing_roles_are_reused_without_mutation()
    {
        var second = MockDataFixtures.Create(TestPasswords, _passwordHasher);
        Assert.That(IdentityValues(second), Is.EquivalentTo(IdentityValues(Fixture)));
        Assert.That(IdentityValues(Fixture).Distinct().Count(), Is.EqualTo(IdentityValues(Fixture).Count()));

        var existingRoles = new[]
        {
            new Role { RoleId = Guid.NewGuid(), Name = "Admin", Description = "Existing Admin role" },
            new Role { RoleId = Guid.NewGuid(), Name = "Staff", Description = "Existing Staff role" }
        };
        var reused = MockDataFixtures.Create(TestPasswords, _passwordHasher, existingRoles);

        foreach (var role in existingRoles)
        {
            Assert.Multiple(() =>
            {
                Assert.That(reused.Roles.Single(candidate => candidate.Name == role.Name), Is.SameAs(role));
                Assert.That(role.Description, Is.EqualTo($"Existing {role.Name} role"));
                Assert.That(reused.Users.Any(user => user.RoleId == role.RoleId), Is.True);
            });
        }

        Assert.That(reused.Users.All(user => existingRoles.Any(role => role.RoleId == user.RoleId)), Is.True);
    }

    [Test]
    public void Creating_fixtures_requires_both_passwords_from_configuration()
    {
        foreach (var options in new[]
                 {
                     new MockDataOptions(),
                     new MockDataOptions { AdminPassword = TestPasswords.AdminPassword },
                     new MockDataOptions { StaffPassword = TestPasswords.StaffPassword },
                     new MockDataOptions { AdminPassword = " ", StaffPassword = TestPasswords.StaffPassword }
                 })
        {
            Assert.Throws<InvalidOperationException>(() => MockDataFixtures.Create(options, _passwordHasher));
        }
    }

    private static IEnumerable<string> IdentityValues(MockDataSet data) =>
        data.Roles.Select(role => $"Role:{role.RoleId}")
            .Concat(data.Users.Select(user => $"User:{user.UserId}"))
            .Concat(data.Products.Select(product => $"Product:{product.ProductId}"))
            .Concat(data.Warehouses.Select(warehouse => $"Warehouse:{warehouse.WarehouseId}"))
            .Concat(data.InboundReceipts.Select(receipt => $"Inbound:{receipt.InboundReceiptId}"))
            .Concat(data.InboundReceiptDetails.Select(line => $"InboundLine:{line.InboundDetailId}"))
            .Concat(data.OutboundReceipts.Select(receipt => $"Outbound:{receipt.OutboundReceiptId}"))
            .Concat(data.OutboundReceiptDetails.Select(line => $"OutboundLine:{line.OutboundDetailId}"))
            .Concat(data.StockBalances.Select(balance => $"Stock:{balance.WarehouseId}:{balance.ProductId}"));
}
