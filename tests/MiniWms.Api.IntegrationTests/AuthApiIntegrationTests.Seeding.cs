using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Persistence.Seeding;
using Moq;
using NUnit.Framework;

namespace MiniWms.Api.IntegrationTests;

public sealed partial class AuthApiIntegrationTests
{
    [TestCase("none")]
    [TestCase("admin")]
    [TestCase("both")]
    public async Task Seeder_accepts_empty_business_data_and_preserves_existing_canonical_roles(string roles)
    {
        await EmptySeedDatabase();
        await using (var context = CreateContext())
        {
            if (roles is "admin" or "both") { context.Roles.Add(new Role { Name = "Admin" }); }
            if (roles == "both") { context.Roles.Add(new Role { Name = "Staff" }); }
            await context.SaveChangesAsync();
        }
        var hasher = SeedHasher();
        var first = await RunSeeder(hasher);
        Assert.That(first.IsAlreadySeeded, Is.False);
        Assert.That(first.Counts.Roles, Is.EqualTo(2));
        Assert.That(first.Counts, Is.EqualTo(new MockDataCounts(2, 4, 10, 3, 3, 12, 2, 6, 10)));
        hasher.Verify(h => h.HashPassword(It.IsAny<User>(), password), Times.Exactly(4));
        await using var check = CreateContext();
        Assert.That(await check.Users.CountAsync(u => u.Role != null && u.Role.Name == "Admin"), Is.EqualTo(1));
        Assert.That(await check.Users.CountAsync(u => u.Role != null && u.Role.Name == "Staff"), Is.EqualTo(3));
    }

    [Test]
    public async Task Seeder_rerun_does_not_reset_passwords_edits_or_audit_timestamps()
    {
        await EmptySeedDatabase();
        await RunSeeder(SeedHasher());
        Guid id;
        DateTimeOffset updated;
        await using (var edit = CreateContext())
        {
            var user = await edit.Users.OrderBy(u => u.Email).FirstAsync();
            id = user.UserId;
            user.PasswordHash = "edited-test-hash";
            user.FullName = "Edited name";
            await edit.SaveChangesAsync();
            await edit.Entry(user).ReloadAsync(); // Compare persisted PostgreSQL microsecond precision.
            updated = user.UpdatedAt;
        }
        var result = await RunSeeder(SeedHasher());
        Assert.That(result.IsAlreadySeeded, Is.True);
        await using var check = CreateContext();
        var persisted = await check.Users.SingleAsync(u => u.UserId == id);
        Assert.That(persisted.PasswordHash, Is.EqualTo("edited-test-hash"));
        Assert.That(persisted.FullName, Is.EqualTo("Edited name"));
        Assert.That(persisted.UpdatedAt, Is.EqualTo(updated));
    }

    [TestCase("role")]
    [TestCase("role-id")]
    [TestCase("user")]
    [TestCase("product")]
    [TestCase("warehouse")]
    public async Task Seeder_refuses_partial_or_conflicting_data_without_overwriting_it(string conflict)
    {
        await EmptySeedDatabase();
        await using (var context = CreateContext())
        {
            if (conflict == "role") { context.Roles.Add(new Role { Name = "Reader" }); }
            if (conflict == "role-id")
                context.Roles.Add(new Role { Name = "Admin", RoleId = Guid.Parse("00000001-0000-0000-0000-000000000002") });
            if (conflict == "user")
            {
                var role = new Role { Name = "Staff" };
                context.Roles.Add(role);
                context.Users.Add(new User { RoleId = role.RoleId, Email = "unrelated@example.test", FullName = "Unrelated", PasswordHash = "unit-hash" });
            }
            if (conflict == "product") { context.Products.Add(new Product { SKU = "UNRELATED", Name = "Unrelated" }); }
            if (conflict == "warehouse") { context.Warehouses.Add(new Warehouse { Code = "UNRELATED", Name = "Unrelated", Address = "Address" }); }
            await context.SaveChangesAsync();
        }
        Assert.ThrowsAsync<InvalidOperationException>(() => RunSeeder(SeedHasher()));
        await using var check = CreateContext();
        Assert.That(await check.InboundReceipts.CountAsync(), Is.Zero);
        Assert.That(await check.OutboundReceipts.CountAsync(), Is.Zero);
    }

    [TestCase("users")]
    [TestCase("products")]
    [TestCase("warehouses")]
    [TestCase("inbounds")]
    [TestCase("outbounds")]
    [TestCase("inbound-lines")]
    [TestCase("outbound-lines")]
    [TestCase("stock")]
    public async Task Seeder_detects_changed_identifiers_even_when_all_table_counts_match(string table)
    {
        await EmptySeedDatabase();
        await RunSeeder(SeedHasher());
        await using (var context = CreateContext())
        {
            switch (table)
            {
                case "users": (await context.Users.OrderBy(u => u.Email).FirstAsync()).Email = "different@example.test"; break;
                case "products": (await context.Products.OrderBy(p => p.SKU).FirstAsync()).SKU = "DIFFERENT"; break;
                case "warehouses": (await context.Warehouses.OrderBy(w => w.Code).FirstAsync()).Code = "DIFFERENT"; break;
                case "inbounds": (await context.InboundReceipts.OrderBy(r => r.ReceiptCode).FirstAsync()).ReceiptCode = "DIFFERENT"; break;
                case "outbounds": (await context.OutboundReceipts.OrderBy(r => r.ReceiptCode).FirstAsync()).ReceiptCode = "DIFFERENT"; break;
                case "inbound-lines":
                {
                    var id = await context.InboundReceiptDetails.Select(d => d.InboundDetailId).FirstAsync();
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"InboundReceiptDetails\" SET \"InboundDetailId\" = {Guid.NewGuid()} WHERE \"InboundDetailId\" = {id}");
                    break;
                }
                case "outbound-lines":
                {
                    var id = await context.OutboundReceiptDetails.Select(d => d.OutboundDetailId).FirstAsync();
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"OutboundReceiptDetails\" SET \"OutboundDetailId\" = {Guid.NewGuid()} WHERE \"OutboundDetailId\" = {id}");
                    break;
                }
                case "stock":
                {
                    var balance = await context.StockBalances.AsNoTracking().FirstAsync();
                    var candidate = await context.Products.Where(p =>
                        !context.StockBalances.Any(b => b.WarehouseId == balance.WarehouseId && b.ProductId == p.ProductId))
                        .Select(p => p.ProductId).FirstAsync();
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"StockBalances\" SET \"ProductId\" = {candidate} WHERE \"WarehouseId\" = {balance.WarehouseId} AND \"ProductId\" = {balance.ProductId}");
                    break;
                }
            }
            await context.SaveChangesAsync();
        }
        Assert.ThrowsAsync<InvalidOperationException>(() => RunSeeder(SeedHasher()));
        await using var check = CreateContext();
        Assert.That(await check.Users.CountAsync(), Is.EqualTo(4));
        Assert.That(await check.Products.CountAsync(), Is.EqualTo(10));
        Assert.That(await check.StockBalances.CountAsync(), Is.EqualTo(10));
    }

    [Test]
    public async Task Seeder_rejects_missing_options_passwords_and_a_dirty_context_before_writing()
    {
        await using var context = CreateContext();
        var seeder = new MockDataSeeder(context, SeedHasher().Object);
        Assert.ThrowsAsync<ArgumentNullException>(() => seeder.SeedAsync(null!));
        Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync(new MockDataOptions()));
        Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync(new MockDataOptions { AdminPassword = password }));
        context.Products.Add(new Product { SKU = "PENDING", Name = "Pending" });
        Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync(SeedOptions()));
    }

    [Test]
    public async Task Seed_entry_point_creates_then_recognizes_the_fixture_only_in_Development()
    {
        await EmptySeedDatabase();
        var entry = typeof(Program).GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing application entry point.");
        string[] arguments =
        [
            "--seed-mock-data", "--environment", "Development",
            "--ConnectionStrings:WmsDatabase", databaseConnection,
            "--MockData:AdminPassword", password, "--MockData:StaffPassword", password,
            "--Logging:LogLevel:Default", "Warning"
        ];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var task = entry.Invoke(null, [arguments]) as Task ?? throw new InvalidOperationException();
            await task;
        }
        await using var check = CreateContext();
        Assert.That(await check.Users.CountAsync(), Is.EqualTo(4));
        Assert.That(await check.StockBalances.CountAsync(), Is.EqualTo(10));
    }


    [Test]
    public async Task Seed_entry_point_disposes_its_scope_when_seeding_fails()
    {
        await EmptySeedDatabase();
        var entry = typeof(Program).GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing application entry point.");
        string[] arguments =
        [
            "--seed-mock-data", "--environment", "Development",
            "--ConnectionStrings:WmsDatabase", databaseConnection,
            "--MockData:AdminPassword", "",
            "--MockData:StaffPassword", password,
            "--Logging:LogLevel:Default", "Warning"
        ];
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var task = entry.Invoke(null, [arguments]) as Task ?? throw new InvalidOperationException();
            await task;
        });
        Assert.That(error?.Message, Does.Contain("MockData"));
        await using var check = CreateContext();
        Assert.That(await check.Users.CountAsync(), Is.Zero);
    }


    [TestCase("admin")]
    [TestCase("staff")]
    public async Task Seed_entry_point_rejects_each_missing_password_without_depending_on_local_User_Secrets(string missing)
    {
        await EmptySeedDatabase();
        var entry = typeof(Program).GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing application entry point.");
        var arguments = new List<string>
        {
            "--seed-mock-data", "--environment", "Development",
            "--applicationName", typeof(AuthApiIntegrationTests).Assembly.GetName().Name ?? throw new InvalidOperationException(),
            "--ConnectionStrings:WmsDatabase", databaseConnection,
            "--Logging:LogLevel:Default", "Warning"
        };
        if (missing != "admin") { arguments.AddRange(["--MockData:AdminPassword", password]); }
        if (missing != "staff") { arguments.AddRange(["--MockData:StaffPassword", password]); }
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var task = entry.Invoke(null, [arguments.ToArray()]) as Task ?? throw new InvalidOperationException();
            await task;
        });
        Assert.That(error?.Message, Does.Contain("MockData"));
    }

    private async Task EmptySeedDatabase()
    {
        await using var context = CreateContext(); // Guarded disposable database only.
        await context.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"RefreshTokens\", \"AuthSessions\", \"Users\", \"Roles\", \"Products\", \"Warehouses\" CASCADE");
    }

    private MockDataOptions SeedOptions() => new() { AdminPassword = password, StaffPassword = password };
    private Mock<IPasswordHasher<User>> SeedHasher()
    {
        var hasher = new Mock<IPasswordHasher<User>>(MockBehavior.Strict);
        hasher.Setup(h => h.HashPassword(It.IsAny<User>(), password)).Returns((User user, string _) => "unit-hash:" + user.Email);
        return hasher;
    }
    private async Task<MockDataSeedResult> RunSeeder(Mock<IPasswordHasher<User>> hasher)
    {
        await using var context = CreateContext();
        return await new MockDataSeeder(context, hasher.Object).SeedAsync(SeedOptions());
    }
}
