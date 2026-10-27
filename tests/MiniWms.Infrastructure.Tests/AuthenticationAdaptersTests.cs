using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using MiniWms.Application.Authentication;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Authentication;
using NUnit.Framework;

namespace MiniWms.Infrastructure.Tests;

[TestFixture]
[Category("Unit")]
public sealed class AuthenticationAdaptersTests
{
    [Test]
    public void Refresh_tokens_round_trip_and_only_the_hash_is_exposed_to_persistence()
    {
        var codec = new OpaqueRefreshTokenCodec();
        var userId = Guid.NewGuid();
        var material = codec.Create(userId);
        var parsed = codec.Parse(material.Token) ?? throw new InvalidOperationException();
        Assert.Multiple(() =>
        {
            Assert.That(parsed.UserId, Is.EqualTo(userId));
            Assert.That(parsed.TokenId, Is.EqualTo(material.TokenId));
            Assert.That(material.SecretHash.Length, Is.EqualTo(64));
            Assert.That(codec.Matches(parsed.SecretHash, material.SecretHash), Is.True);
            Assert.That(material.SecretHash.Contains(material.Token, StringComparison.Ordinal), Is.False);
        });
    }

    [Test]
    public void Altered_secrets_and_noncanonical_base64_are_rejected()
    {
        var codec = new OpaqueRefreshTokenCodec();
        var material = codec.Create(Guid.NewGuid());
        var parts = material.Token.Split('.');
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var last = alphabet.IndexOf(parts[2][^1]);
        parts[2] = parts[2][..^1] + alphabet[last + 1];
        Assert.That(codec.Parse(string.Join('.', parts)) is null, Is.True);
        parts = material.Token.Split('.');
        parts[2] = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var altered = codec.Parse(string.Join('.', parts)) ?? throw new InvalidOperationException();
        Assert.That(codec.Matches(altered.SecretHash, material.SecretHash), Is.False);
    }

    [TestCase("")]
    [TestCase("not-a-token")]
    [TestCase("00000000000000000000000000000000.invalid.invalid")]
    public void Invalid_refresh_token_formats_are_not_accepted(string token) =>
        Assert.That(new OpaqueRefreshTokenCodec().Parse(token) is null, Is.True);

    [Test]
    public void Access_token_signature_claims_and_session_deadline_are_enforced_by_the_real_JWT_adapter()
    {
        var settings = new JwtSettings { Issuer = "test-issuer", Audience = "test-audience", SigningKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) };
        var issuer = new JwtAuthTokenIssuer(settings);
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var user = new AuthUserDetails(Guid.NewGuid(), "adapter@integration.test", "Adapter account", "Staff");
        var session = new AuthSession { UserId = user.UserId, SessionId = Guid.NewGuid(), AuthVersion = 7, ExpiresAt = now.AddSeconds(10) };
        var token = issuer.Create(user, session, now, TimeSpan.FromMinutes(15));
        var validation = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = settings.Issuer,
            ValidateAudience = true, ValidAudience = settings.Audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(settings.GetSigningKeyBytes()),
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero
        };
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token.Token, validation, out _);
        Assert.Multiple(() =>
        {
            Assert.That(token.ExpiresAt, Is.EqualTo(session.ExpiresAt));
            Assert.That(principal.FindFirst("sub")?.Value, Is.EqualTo(user.UserId.ToString("D")));
            Assert.That(principal.FindFirst("sid")?.Value, Is.EqualTo(session.SessionId.ToString("D")));
            Assert.That(principal.FindFirst("auth_version")?.Value, Is.EqualTo("7"));
            Assert.That(principal.FindFirst("role")?.Value, Is.EqualTo("Staff"));
        });
    }

    [Test]
    public void JWT_configuration_rejects_missing_or_short_signing_keys()
    {
        Assert.Throws<InvalidOperationException>(() => new JwtAuthTokenIssuer(new JwtSettings()));
        Assert.Throws<InvalidOperationException>(() => new JwtAuthTokenIssuer(new JwtSettings { SigningKeyBase64 = Convert.ToBase64String(new byte[16]) }));
    }
}
