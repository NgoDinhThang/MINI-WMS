using System.Data;
using System.Text.RegularExpressions;
using MiniWms.Domain.Common;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;

namespace MiniWms.Infrastructure.Tests;

[TestFixture]
[Category("Unit")]
public sealed class WmsModelTests
{
    private static readonly Dictionary<Type, (string Table, string[] Properties)> ErdContract = new()
    {
        [typeof(AuthSession)] = ("AuthSessions", ["SessionId", "UserId", "AuthVersion", "ExpiresAt", "RevokedAt"]),
        [typeof(RefreshToken)] = ("RefreshTokens", ["RefreshTokenId", "SessionId", "SecretHash", "ExpiresAt", "ConsumedAt"]),
        [typeof(Role)] = ("Roles", ["RoleId", "Name", "Description"]),
        [typeof(User)] = ("Users", ["UserId", "RoleId", "FullName", "Email", "PasswordHash", "IsActive", "AuthVersion"]),
        [typeof(Product)] = ("Products", ["ProductId", "SKU", "Name", "Description", "IsActive"]),
        [typeof(Warehouse)] = ("Warehouses", ["WarehouseId", "Code", "Name", "Address", "IsActive"]),
        [typeof(StockBalance)] = ("StockBalances", ["WarehouseId", "ProductId", "Quantity", "Version"]),
        [typeof(InboundReceipt)] = ("InboundReceipts", ["InboundReceiptId", "ReceiptCode", "WarehouseId", "UserId", "ReceivedAt", "Note"]),
        [typeof(InboundReceiptDetail)] = ("InboundReceiptDetails", ["InboundDetailId", "InboundReceiptId", "ProductId", "Quantity"]),
        [typeof(OutboundReceipt)] = ("OutboundReceipts", ["OutboundReceiptId", "ReceiptCode", "WarehouseId", "UserId", "RecipientName", "IssuedAt", "Note"]),
        [typeof(OutboundReceiptDetail)] = ("OutboundReceiptDetails", ["OutboundDetailId", "OutboundReceiptId", "ProductId", "Quantity"])
    };

    [Test]
    public void Model_preserves_erd_fields_and_adds_authentication_state_with_audit_timestamps()
    {
        using var context = CreateContext();

        Assert.That(context.Model.GetEntityTypes().Select(entity => entity.ClrType),
            Is.EquivalentTo(ErdContract.Keys));

        foreach (var (type, contract) in ErdContract)
        {
            var entity = GetEntity(context, type);
            Assert.Multiple(() =>
            {
                Assert.That(entity.GetTableName(), Is.EqualTo(contract.Table));
                Assert.That(entity.GetProperties().Select(property => property.Name),
                    Is.EquivalentTo(contract.Properties.Concat(["CreatedAt", "UpdatedAt"])), contract.Table);
            });
        }

        var shadowProperties = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.IsShadowProperty())
            .Select(property => $"{property.DeclaringType.ClrType.Name}.{property.Name}");

        Assert.That(shadowProperties, Is.EquivalentTo(new[] { "StockBalance.Version" }));
    }

    [Test]
    public void Every_business_table_has_required_utc_audit_timestamps_and_database_defaults()
    {
        using var context = CreateContext();
        var sql = context.Database.GenerateCreateScript();

        foreach (var entity in context.Model.GetEntityTypes())
        {
            Assert.That(typeof(IAuditableEntity).IsAssignableFrom(entity.ClrType), Is.True, entity.Name);
            foreach (var name in new[] { "CreatedAt", "UpdatedAt" })
            {
                var property = GetProperty(context, entity.ClrType, name);
                Assert.Multiple(() =>
                {
                    Assert.That(property.IsShadowProperty(), Is.False, $"{entity.Name}.{name}");
                    Assert.That(property.ClrType, Is.EqualTo(typeof(DateTimeOffset)));
                    Assert.That(property.IsNullable, Is.False);
                    Assert.That(property.GetColumnType(), Is.EqualTo("timestamp with time zone"));
                    Assert.That(property.GetDefaultValueSql(), Is.EqualTo("CURRENT_TIMESTAMP"));
                });
            }

            var tableDefinition = Regex.Match(sql,
                $"CREATE TABLE \"{entity.GetTableName()}\" \\((?<body>[\\s\\S]*?)\\n\\);", RegexOptions.CultureInvariant);
            Assert.That(tableDefinition.Success, Is.True);
            foreach (var name in new[] { "CreatedAt", "UpdatedAt" })
            {
                Assert.That(Regex.IsMatch(tableDefinition.Groups["body"].Value,
                        $"\"{name}\" timestamp with time zone NOT NULL DEFAULT \\(?CURRENT_TIMESTAMP\\)?",
                        RegexOptions.CultureInvariant),
                    Is.True, entity.GetTableName());
            }
        }

        Assert.That(context.Database.GetDbConnection().State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void Relationships_preserve_erd_and_authentication_foreign_keys()
    {
        using var context = CreateContext();
        var foreignKeys = context.Model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()).ToArray();

        string[] expected =
        [
            "AuthSessions:UserId->Users",
            "RefreshTokens:SessionId->AuthSessions",
            "Users:RoleId->Roles",
            "StockBalances:WarehouseId->Warehouses",
            "StockBalances:ProductId->Products",
            "InboundReceipts:WarehouseId->Warehouses",
            "InboundReceipts:UserId->Users",
            "OutboundReceipts:WarehouseId->Warehouses",
            "OutboundReceipts:UserId->Users",
            "InboundReceiptDetails:InboundReceiptId->InboundReceipts",
            "InboundReceiptDetails:ProductId->Products",
            "OutboundReceiptDetails:OutboundReceiptId->OutboundReceipts",
            "OutboundReceiptDetails:ProductId->Products"
        ];

        Assert.That(foreignKeys.Select(foreignKey =>
                $"{foreignKey.DeclaringEntityType.GetTableName()}:{string.Join(",", foreignKey.Properties.Select(property => property.Name))}->{foreignKey.PrincipalEntityType.GetTableName()}"),
            Is.EquivalentTo(expected));

        foreach (var foreignKey in foreignKeys)
        {
            Assert.Multiple(() =>
            {
                Assert.That(foreignKey.IsRequired, Is.True);
                Assert.That(foreignKey.IsUnique, Is.False);
                Assert.That(foreignKey.DeleteBehavior, Is.EqualTo(DeleteBehavior.Restrict));
                Assert.That(foreignKey.DependentToPrincipal, Is.Not.Null);
                Assert.That(foreignKey.PrincipalToDependent?.IsCollection, Is.True);
                Assert.That(foreignKey.Properties.All(property => !property.IsShadowProperty()), Is.True);
            });
        }
    }

    [Test]
    public void Keys_and_unique_indexes_prevent_duplicate_stock_codes_emails_and_product_lines()
    {
        using var context = CreateContext();
        var stock = GetEntity(context, typeof(StockBalance));

        Assert.That(stock.FindPrimaryKey()?.Properties.Select(property => property.Name),
            Is.EqualTo(new[] { "WarehouseId", "ProductId" }));

        foreach (var entity in context.Model.GetEntityTypes())
        {
            var primaryKey = entity.FindPrimaryKey()
                ?? throw new InvalidOperationException($"Missing primary key for {entity.Name}.");

            Assert.That(primaryKey.Properties.All(property => property.ClrType == typeof(Guid)), Is.True);
            if (entity.ClrType != typeof(StockBalance))
            {
                Assert.That(primaryKey.Properties, Has.Count.EqualTo(1));
            }
        }

        var uniqueIndexes = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes().Where(index => index.IsUnique)
                .Select(index => $"{entity.GetTableName()}:{string.Join(",", index.Properties.Select(property => property.Name))}"));

        Assert.That(uniqueIndexes, Is.EquivalentTo(new[]
        {
            "AuthSessions:UserId", "AuthSessions:UserId,AuthVersion", "RefreshTokens:SecretHash",
            "Roles:Name", "Users:Email", "Products:SKU", "Warehouses:Code",
            "InboundReceipts:ReceiptCode", "OutboundReceipts:ReceiptCode",
            "InboundReceiptDetails:InboundReceiptId,ProductId",
            "OutboundReceiptDetails:OutboundReceiptId,ProductId"
        }));
        Assert.That(stock.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { "ProductId" })), Is.True);
    }

    [Test]
    public void Only_optional_business_fields_and_authentication_state_timestamps_are_nullable()
    {
        using var context = CreateContext();
        HashSet<(Type Type, string Property)> optionalFields =
        [
            (typeof(AuthSession), "RevokedAt"),
            (typeof(RefreshToken), "ConsumedAt"),
            (typeof(Role), "Description"),
            (typeof(Product), "Description"),
            (typeof(InboundReceipt), "Note"),
            (typeof(OutboundReceipt), "Note")
        ];

        foreach (var entity in context.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                Assert.That(property.IsNullable,
                    Is.EqualTo(optionalFields.Contains((entity.ClrType, property.Name))),
                    $"{entity.GetTableName()}.{property.Name}");
            }
        }
    }

    [Test]
    public void Quantities_allow_three_fractional_digits_and_document_dates_use_postgresql_timestamps()
    {
        using var context = CreateContext();

        foreach (var type in new[] { typeof(StockBalance), typeof(InboundReceiptDetail), typeof(OutboundReceiptDetail) })
        {
            var quantity = GetProperty(context, type, "Quantity");
            Assert.Multiple(() =>
            {
                Assert.That(quantity.ClrType, Is.EqualTo(typeof(decimal)));
                Assert.That(quantity.GetPrecision(), Is.EqualTo(18));
                Assert.That(quantity.GetScale(), Is.EqualTo(3));
            });
        }

        foreach (var (type, name) in new[] { (typeof(InboundReceipt), "ReceivedAt"), (typeof(OutboundReceipt), "IssuedAt") })
        {
            var date = GetProperty(context, type, name);
            Assert.Multiple(() =>
            {
                Assert.That(date.ClrType, Is.EqualTo(typeof(DateTimeOffset)));
                Assert.That(date.GetColumnType(), Is.EqualTo("timestamp with time zone"));
            });
        }
    }

    [Test]
    public void Stock_uses_the_postgresql_xmin_concurrency_token_without_a_new_domain_field()
    {
        using var context = CreateContext();
        var stock = GetEntity(context, typeof(StockBalance));
        var version = GetProperty(context, typeof(StockBalance), "Version");
        var table = StoreObjectIdentifier.Table("StockBalances", stock.GetSchema());

        Assert.Multiple(() =>
        {
            Assert.That(version.IsShadowProperty(), Is.True);
            Assert.That(version.ClrType, Is.EqualTo(typeof(uint)));
            Assert.That(version.IsConcurrencyToken, Is.True);
            Assert.That(version.ValueGenerated, Is.EqualTo(ValueGenerated.OnAddOrUpdate));
            Assert.That(version.GetColumnName(table), Is.EqualTo("xmin"));
            Assert.That(version.GetColumnType(), Is.EqualTo("xid"));
        });
    }

    [Test]
    public void Generated_postgresql_schema_rejects_invalid_quantities_without_opening_a_connection()
    {
        using var context = CreateContext();
        var sql = context.Database.GenerateCreateScript();

        foreach (var (table, comparison) in new[]
                 {
                     ("StockBalances", ">="),
                     ("InboundReceiptDetails", ">"),
                     ("OutboundReceiptDetails", ">")
                 })
        {
            var tableDefinition = Regex.Match(sql,
                $"CREATE TABLE \"{table}\" \\((?<body>[\\s\\S]*?)\\n\\);", RegexOptions.CultureInvariant);

            Assert.That(tableDefinition.Success, Is.True, $"Missing CREATE TABLE for {table}.");
            Assert.That(Regex.IsMatch(tableDefinition.Groups["body"].Value,
                $"CHECK\\s*\\(\\s*\"Quantity\"\\s*{comparison}\\s*0\\s*\\)", RegexOptions.CultureInvariant),
                Is.True, $"Missing quantity constraint for {table}.");
        }

        Assert.That(context.Database.GetDbConnection().State, Is.EqualTo(ConnectionState.Closed));
    }

    [Test]
    public void Authentication_model_enforces_one_unrevoked_session_and_hash_only_refresh_storage()
    {
        using var context = CreateContext();
        var session = GetEntity(context, typeof(AuthSession));
        var activeIndex = session.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { "UserId" }));
        var hash = GetProperty(context, typeof(RefreshToken), "SecretHash");
        var sql = context.Database.GenerateCreateScript();
        Assert.Multiple(() =>
        {
            Assert.That(activeIndex.IsUnique, Is.True);
            Assert.That(activeIndex.GetFilter(), Is.EqualTo("\"RevokedAt\" IS NULL"));
            Assert.That(GetProperty(context, typeof(User), "AuthVersion").IsConcurrencyToken, Is.True);
            Assert.That(hash.GetMaxLength(), Is.EqualTo(64));
            Assert.That(sql.Contains("CK_RefreshTokens_SecretHash_Format", StringComparison.Ordinal), Is.True);
            Assert.That(sql.Contains("CK_Users_AuthVersion_Nonnegative", StringComparison.Ordinal), Is.True);
        });
    }
    private static WmsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WmsDbContext>()
            .UseNpgsql("Host=localhost;Database=miniwms_model_tests;Username=model_only;Password=unused")
            .Options;

        return new WmsDbContext(options);
    }

    private static IEntityType GetEntity(WmsDbContext context, Type type) =>
        context.Model.FindEntityType(type)
        ?? throw new InvalidOperationException($"Missing entity {type.Name}.");

    private static IProperty GetProperty(WmsDbContext context, Type type, string name) =>
        GetEntity(context, type).FindProperty(name)
        ?? throw new InvalidOperationException($"Missing property {type.Name}.{name}.");
}
