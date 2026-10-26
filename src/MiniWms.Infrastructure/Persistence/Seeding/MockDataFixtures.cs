using MiniWms.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace MiniWms.Infrastructure.Persistence.Seeding;

public static class MockDataFixtures
{
    public static MockDataSet Create(
        MockDataOptions options,
        IPasswordHasher<User> passwordHasher,
        IReadOnlyCollection<Role>? existingRoles = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        options.Validate();

        var roles = new[]
        {
            FindOrCreateRole("Admin", 1, "Administrator with all warehouse permissions."),
            FindOrCreateRole("Staff", 2, "Warehouse staff with receipt, issue, and lookup permissions.")
        };
        var users = new[]
        {
            CreateUser(1, "Demo Administrator", "admin@mini-wms.test", roles[0], true),
            CreateUser(2, "Alice Warehouse", "staff1@mini-wms.test", roles[1], true),
            CreateUser(3, "Ben Warehouse", "staff2@mini-wms.test", roles[1], true),
            CreateUser(4, "Casey Warehouse", "staff.banned@mini-wms.test", roles[1], false)
        };
        var productNames = new[]
        {
            "Laptop stand", "Wireless keyboard", "Network cable", "USB hub", "Packing tape",
            "Shipping carton", "Protective sleeve", "Packing paper", "Legacy adapter", "Retired label roll"
        };
        var products = productNames.Select((name, index) => new Product
        {
            ProductId = Key(3, index + 1),
            SKU = $"DEMO-SKU-{index + 1:000}",
            Name = name,
            Description = "Development fixture for Mini WMS. Physical quantity units remain undefined.",
            IsActive = index < 8
        }).ToArray();
        var warehouses = new[]
        {
            new Warehouse
            {
                WarehouseId = Key(4, 1), Code = "DEMO-WH-HCM", Name = "Ho Chi Minh Warehouse",
                Address = "100 Example Street, Ho Chi Minh City", IsActive = true
            },
            new Warehouse
            {
                WarehouseId = Key(4, 2), Code = "DEMO-WH-HN", Name = "Hanoi Warehouse",
                Address = "200 Example Street, Hanoi", IsActive = true
            },
            new Warehouse
            {
                WarehouseId = Key(4, 3), Code = "DEMO-WH-OLD", Name = "Retired Warehouse",
                Address = "300 Example Street, Da Nang", IsActive = false
            }
        };
        var inbounds = new[]
        {
            CreateInbound(1, 0, 1, 1, "Opening delivery for Ho Chi Minh City."),
            CreateInbound(2, 1, 2, 2, "Opening delivery for Hanoi."),
            CreateInbound(3, 0, 2, 4, "Additional packing and cable delivery.")
        };
        var inboundLines = new List<InboundReceiptDetail>();
        AddInboundLines(inbounds[0], (0, 100m), (1, 80m), (2, 250.500m), (3, 40m), (4, 60m), (5, 20m));
        AddInboundLines(inbounds[1], (0, 50m), (1, 35m), (6, 100m), (7, 18.750m));
        AddInboundLines(inbounds[2], (2, 25m), (4, 15m));

        var outbounds = new[]
        {
            CreateOutbound(1, 0, 1, 6, "Example Office Supplies", "Delivery to a development customer."),
            CreateOutbound(2, 1, 2, 11, "Example Retail Store", "Delivery from Hanoi.")
        };
        var outboundLines = new List<OutboundReceiptDetail>();
        AddOutboundLines(outbounds[0], (0, 12m), (2, 45.125m), (5, 20m));
        AddOutboundLines(outbounds[1], (1, 5m), (6, 10m), (7, 2.250m));

        // Derive balances from the fixture ledger so documents and current stock cannot drift.
        var movementTotals = new Dictionary<(Guid WarehouseId, Guid ProductId), decimal>();
        foreach (var receipt in inbounds)
        {
            foreach (var line in receipt.Details)
            {
                Accumulate(receipt.WarehouseId, line.ProductId, line.Quantity);
            }
        }
        foreach (var receipt in outbounds)
        {
            foreach (var line in receipt.Details)
            {
                Accumulate(receipt.WarehouseId, line.ProductId, -line.Quantity);
            }
        }
        var stocks = movementTotals.Select(pair => new StockBalance
        {
            WarehouseId = pair.Key.WarehouseId,
            ProductId = pair.Key.ProductId,
            Quantity = pair.Value
        }).ToArray();
        if (stocks.Any(stock => stock.Quantity < 0))
        {
            throw new InvalidOperationException("The development fixture ledger produces negative stock.");
        }

        return new MockDataSet(roles, users, products, warehouses, inbounds, inboundLines,
            outbounds, outboundLines, stocks);

        Role FindOrCreateRole(string name, int number, string description) =>
            existingRoles?.SingleOrDefault(role => role.Name == name) ?? new Role
            {
                RoleId = Key(1, number), Name = name, Description = description
            };

        User CreateUser(int number, string name, string email, Role role, bool isActive)
        {
            var user = new User
            {
                UserId = Key(2, number), RoleId = role.RoleId, FullName = name,
                Email = email, IsActive = isActive
            };
            user.PasswordHash = passwordHasher.HashPassword(user,
                role.Name == "Admin" ? options.AdminPassword : options.StaffPassword);
            return user;
        }

        InboundReceipt CreateInbound(int number, int warehouse, int user, int day, string note) => new()
        {
            InboundReceiptId = Key(5, number), ReceiptCode = $"DEMO-IN-{number:000}",
            WarehouseId = warehouses[warehouse].WarehouseId, UserId = users[user].UserId,
            ReceivedAt = new DateTimeOffset(2026, 10, day, 8, 0, 0, TimeSpan.Zero), Note = note
        };

        OutboundReceipt CreateOutbound(int number, int warehouse, int user, int day, string recipient, string note) => new()
        {
            OutboundReceiptId = Key(7, number), ReceiptCode = $"DEMO-OUT-{number:000}",
            WarehouseId = warehouses[warehouse].WarehouseId, UserId = users[user].UserId,
            IssuedAt = new DateTimeOffset(2026, 10, day, 9, 0, 0, TimeSpan.Zero),
            RecipientName = recipient, Note = note
        };

        void AddInboundLines(InboundReceipt receipt, params (int Product, decimal Quantity)[] lines)
        {
            foreach (var line in lines)
            {
                var detail = new InboundReceiptDetail
                {
                    InboundDetailId = Key(6, inboundLines.Count + 1),
                    InboundReceiptId = receipt.InboundReceiptId,
                    ProductId = products[line.Product].ProductId, Quantity = line.Quantity
                };
                receipt.Details.Add(detail);
                inboundLines.Add(detail);
            }
        }

        void AddOutboundLines(OutboundReceipt receipt, params (int Product, decimal Quantity)[] lines)
        {
            foreach (var line in lines)
            {
                var detail = new OutboundReceiptDetail
                {
                    OutboundDetailId = Key(8, outboundLines.Count + 1),
                    OutboundReceiptId = receipt.OutboundReceiptId,
                    ProductId = products[line.Product].ProductId, Quantity = line.Quantity
                };
                receipt.Details.Add(detail);
                outboundLines.Add(detail);
            }
        }

        void Accumulate(Guid warehouseId, Guid productId, decimal quantity)
        {
            var key = (warehouseId, productId);
            movementTotals[key] = movementTotals.GetValueOrDefault(key) + quantity;
        }
    }

    private static Guid Key(int category, int number) =>
        Guid.Parse($"{category:00000000}-0000-0000-0000-{number:000000000000}");
}
