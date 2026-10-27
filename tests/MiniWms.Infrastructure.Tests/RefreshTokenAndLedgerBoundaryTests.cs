using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using MiniWms.Infrastructure.Authentication;
using MiniWms.Infrastructure.Persistence.Seeding;
using NUnit.Framework;

namespace MiniWms.Infrastructure.Tests;

[TestFixture]
[Category("Unit")]
public sealed class RefreshTokenAndLedgerBoundaryTests
{
    [Test]
    public void Refresh_credential_creation_requires_a_real_user_identifier() =>
        Assert.Throws<ArgumentException>(() => new OpaqueRefreshTokenCodec().Create(Guid.Empty));

    [TestCase("parts")]
    [TestCase("user-format")]
    [TestCase("user-empty")]
    [TestCase("token-format")]
    [TestCase("token-empty")]
    [TestCase("secret-character")]
    [TestCase("noncanonical")]
    public void Malformed_credentials_are_rejected_at_each_format_boundary(string defect)
    {
        var codec = new OpaqueRefreshTokenCodec();
        var material = codec.Create(Guid.NewGuid());
        var parts = material.Token.Split('.');
        switch (defect)
        {
            case "parts":
                Assert.That(codec.Parse(new string('a', 109)), Is.Null);
                return;
            case "user-format": parts[0] = new string('z', 32); break;
            case "user-empty": parts[0] = Guid.Empty.ToString("N"); break;
            case "token-format": parts[1] = new string('z', 32); break;
            case "token-empty": parts[1] = Guid.Empty.ToString("N"); break;
            case "secret-character": parts[2] = "!" + parts[2][1..]; break;
            case "noncanonical":
                const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
                var last = alphabet.IndexOf(parts[2][^1]);
                parts[2] = parts[2][..^1] + alphabet[last + 1];
                break;
        }
        Assert.That(codec.Parse(string.Join('.', parts)), Is.Null);
    }

    [TestCase("-")]
    [TestCase("_")]
    [TestCase("A")]
    [TestCase("z")]
    [TestCase("9")]
    public void Every_Base64Url_character_group_is_accepted_when_canonical(string first)
    {
        var codec = new OpaqueRefreshTokenCodec();
        var secret = first + new string('A', 42);
        var user = Guid.NewGuid();
        var token = Guid.NewGuid();
        var parsed = codec.Parse($"{user:N}.{token:N}.{secret}") ?? throw new InvalidOperationException();
        Assert.That(parsed.UserId, Is.EqualTo(user));
        Assert.That(parsed.TokenId, Is.EqualTo(token));
        Assert.That(parsed.SecretHash, Is.EqualTo(Convert.ToHexString(SHA256.HashData(Base64UrlEncoder.DecodeBytes(secret)))));
    }

    [TestCase("", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "")]
    [TestCase("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void Hash_comparison_rejects_bad_lengths_and_non_hex_values(string supplied, string stored) =>
        Assert.That(new OpaqueRefreshTokenCodec().Matches(supplied, stored), Is.False);

    [Test]
    public void Missing_credentials_are_not_parseable() => Assert.That(new OpaqueRefreshTokenCodec().Parse(null!), Is.Null);

    [Test]
    public void Fixture_stock_projection_rejects_a_negative_ledger_and_preserves_valid_pairs()
    {
        var pair = (WarehouseId: Guid.NewGuid(), ProductId: Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => MockDataFixtures.BuildStockBalances(
            new Dictionary<(Guid, Guid), decimal> { [pair] = -1 }));
        var stock = MockDataFixtures.BuildStockBalances(new Dictionary<(Guid, Guid), decimal> { [pair] = 2.125m }).Single();
        Assert.That((stock.WarehouseId, stock.ProductId, stock.Quantity), Is.EqualTo((pair.WarehouseId, pair.ProductId, 2.125m)));
        Assert.That(MockDataFixtures.BuildStockBalances(new Dictionary<(Guid, Guid), decimal>()), Is.Empty);
    }
}
