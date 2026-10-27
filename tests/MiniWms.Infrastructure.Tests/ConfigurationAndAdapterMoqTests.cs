using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniWms.Application.Authentication;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Authentication;
using MiniWms.Infrastructure.Business;
using MiniWms.Infrastructure.Persistence;
using MiniWms.Infrastructure.Persistence.Seeding;
using Moq;
using NUnit.Framework;
using StackExchange.Redis;

namespace MiniWms.Infrastructure.Tests;

[TestFixture]
[Category("Unit")]
[NonParallelizable]
public sealed class ConfigurationAndAdapterMoqTests
{
    private static readonly string Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    [Test]
    public void JWT_configuration_defaults_are_loaded_and_explicit_values_override_them()
    {
        var defaults = JwtSettings.FromConfiguration(Config(new() { ["Jwt:SigningKeyBase64"] = Key }));
        Assert.That(defaults.Issuer, Is.EqualTo("MiniWms.Api"));
        Assert.That(defaults.Audience, Is.EqualTo("MiniWms.Client"));
        var explicitSettings = JwtSettings.FromConfiguration(Config(new()
        {
            ["Jwt:SigningKeyBase64"] = Key, ["Jwt:Issuer"] = "issuer", ["Jwt:Audience"] = "audience"
        }));
        Assert.That(explicitSettings.Issuer, Is.EqualTo("issuer"));
        Assert.That(explicitSettings.Audience, Is.EqualTo("audience"));
        Assert.That(explicitSettings.GetSigningKeyBytes().Length, Is.EqualTo(64));
        Assert.Throws<ArgumentNullException>(() => JwtSettings.FromConfiguration(null!));
        Assert.Throws<InvalidOperationException>(() => JwtSettings.FromConfiguration(Config([])));
    }

    [TestCase("", "audience")]
    [TestCase(" ", "audience")]
    [TestCase("issuer", "")]
    [TestCase("issuer", " ")]
    public void JWT_issuer_and_audience_must_be_present(string issuer, string audience) =>
        Assert.Throws<InvalidOperationException>(() => new JwtSettings { Issuer = issuer, Audience = audience, SigningKeyBase64 = Key }.Validate());

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("not-base64")]
    public void Invalid_signing_key_configuration_is_rejected(string? value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new JwtSettings { SigningKeyBase64 = value! }.GetSigningKeyBytes());
        if (value == "not-base64") { Assert.That(error?.InnerException, Is.TypeOf<FormatException>()); }
    }

    [Test]
    public void Expired_session_is_rejected_by_the_real_issuer()
    {
        var now = DateTimeOffset.UtcNow;
        var issuer = new JwtAuthTokenIssuer(new JwtSettings { SigningKeyBase64 = Key });
        var error = Assert.Throws<AuthException>(() => issuer.Create(new(Guid.NewGuid(), "user@example.test", "User", "Staff"),
            new AuthSession { ExpiresAt = now.AddSeconds(-1) }, now, TimeSpan.FromMinutes(15)));
        Assert.That(error?.Kind, Is.EqualTo(AuthErrorKind.InvalidSession));
    }

    [TestCase(PasswordVerificationResult.Failed, false)]
    [TestCase(PasswordVerificationResult.Success, true)]
    [TestCase(PasswordVerificationResult.SuccessRehashNeeded, true)]
    public void Identity_verifier_obeys_the_hasher_outcome(PasswordVerificationResult verification, bool accepted)
    {
        var user = new User { PasswordHash = "hash" };
        var hasher = new Mock<IPasswordHasher<User>>(MockBehavior.Strict);
        hasher.Setup(h => h.VerifyHashedPassword(user, "hash", "password")).Returns(verification);
        Assert.That(new IdentityAuthPasswordVerifier(hasher.Object).Verify(user, "password"), Is.EqualTo(accepted));
        hasher.VerifyAll();
    }

    [Test]
    public void Missing_user_performs_dummy_verification_but_cannot_authenticate()
    {
        var hasher = new Mock<IPasswordHasher<User>>(MockBehavior.Strict);
        hasher.Setup(h => h.VerifyHashedPassword(It.IsAny<User>(), It.IsAny<string>(), "password")).Returns(PasswordVerificationResult.Success);
        Assert.That(new IdentityAuthPasswordVerifier(hasher.Object).Verify(null, "password"), Is.False);
        hasher.Verify(h => h.VerifyHashedPassword(It.IsAny<User>(), It.Is<string>(s => !string.IsNullOrWhiteSpace(s)), "password"), Times.Once);
    }

    [Test]
    public void Corrupt_identity_hash_is_not_an_unhandled_exception()
    {
        var hasher = new Mock<IPasswordHasher<User>>(MockBehavior.Strict);
        hasher.Setup(h => h.VerifyHashedPassword(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<string>())).Throws<FormatException>();
        Assert.That(new IdentityAuthPasswordVerifier(hasher.Object).Verify(new User(), "password"), Is.False);
    }

    [Test]
    public void Staff_password_adapter_uses_the_supplied_hasher_and_returns_only_its_hash()
    {
        var user = new User();
        var hasher = new Mock<IPasswordHasher<User>>(MockBehavior.Strict);
        hasher.Setup(h => h.HashPassword(user, "initial-password")).Returns("salted-hash");
        Assert.That(new StaffPasswordHasher(hasher.Object).Hash(user, "initial-password"), Is.EqualTo("salted-hash"));
        hasher.VerifyAll();
    }

    [Test]
    public void Infrastructure_registration_requires_services_and_database_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => DependencyInjection.AddInfrastructure(null!, "Host=localhost"));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddInfrastructure(" "));
        var services = new ServiceCollection().AddLogging().AddInfrastructure("Host=127.0.0.1;Database=unit;Username=unit");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<WmsDbContext>().Database.ProviderName, Is.EqualTo("Npgsql.EntityFrameworkCore.PostgreSQL"));
    }

    [Test]
    public void Authentication_registration_requires_valid_inputs_and_Redis_connection_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => AuthenticationRegistration.AddAuthenticationInfrastructure(null!, Config([])));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAuthenticationInfrastructure(null!));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAuthenticationInfrastructure(Config(new() { ["Jwt:SigningKeyBase64"] = Key })));
    }

    [TestCase("Authentication:AccessTokenMinutes", "invalid")]
    [TestCase("Authentication:AccessTokenMinutes", "NaN")]
    [TestCase("Authentication:AccessTokenMinutes", "Infinity")]
    [TestCase("Authentication:AccessTokenMinutes", "0")]
    [TestCase("Authentication:SessionLifetimeHours", "-1")]
    [TestCase("Authentication:SessionLifetimeHours", "9")]
    public void Invalid_authentication_durations_are_rejected_at_registration(string key, string value)
    {
        var settings = BaseSettings();
        settings[key] = value;
        Assert.That(() => Register(settings), value == "9" ? Throws.TypeOf<ArgumentOutOfRangeException>() : Throws.TypeOf<InvalidOperationException>());
    }

    [TestCase(null)]
    [TestCase("ReviewClient")]
    public void Redis_factory_receives_connection_options_and_shared_connection_is_reused(string? clientName)
    {
        var settings = BaseSettings();
        settings["ConnectionStrings:Redis"] += clientName is null ? "" : ",name=" + clientName;
        var factory = new Mock<IRedisConnectionFactory>(MockBehavior.Strict);
        var connection = new Mock<IConnectionMultiplexer>();
        factory.Setup(f => f.Connect(It.Is<ConfigurationOptions>(o =>
            o.ClientName == (clientName ?? "MiniWms.Api") && !o.AbortOnConnectFail && o.ConnectRetry == 1))).Returns(connection.Object);
        var services = Register(settings);
        services.AddSingleton(factory.Object);
        using var provider = services.BuildServiceProvider();
        Assert.That(provider.GetRequiredService<IConnectionMultiplexer>(), Is.SameAs(connection.Object));
        Assert.That(provider.GetRequiredService<IConnectionMultiplexer>(), Is.SameAs(connection.Object));
        Assert.That(provider.GetRequiredService<AuthPolicy>(), Is.EqualTo(AuthPolicy.Default));
        factory.Verify(f => f.Connect(It.IsAny<ConfigurationOptions>()), Times.Once);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Redis_factory_failure_is_mapped_to_authentication_unavailability(bool timeout)
    {
        var factory = new Mock<IRedisConnectionFactory>(MockBehavior.Strict);
        Exception failure = timeout ? new TimeoutException() : new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "test", null, CommandStatus.Unknown);
        factory.Setup(f => f.Connect(It.IsAny<ConfigurationOptions>())).Throws(failure);
        var services = Register(BaseSettings());
        services.AddSingleton(factory.Object);
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<AuthException>(() => provider.GetRequiredService<IConnectionMultiplexer>());
        Assert.That(error?.Kind, Is.EqualTo(AuthErrorKind.Infrastructure));
        Assert.That(error?.InnerException, Is.SameAs(failure));
    }

    [Test]
    public void Explicit_Redis_prefix_and_injected_clock_are_preserved()
    {
        var settings = BaseSettings();
        settings["Redis:KeyPrefix"] = "review:";
        var clock = new Mock<TimeProvider>().Object;
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(clock);
        services.AddAuthenticationInfrastructure(Config(settings));
        using var provider = services.BuildServiceProvider();
        Assert.That(provider.GetRequiredService<RedisAuthSettings>().KeyPrefix, Is.EqualTo("review:"));
        Assert.That(provider.GetRequiredService<TimeProvider>(), Is.SameAs(clock));
    }

    [TestCase(null, "mini_wms")]
    [TestCase("Host=127.0.0.1;Database=design_test;Username=unit", "design_test")]
    public void Design_time_context_uses_environment_or_password_free_fallback(string? connection, string expectedDatabase)
    {
        var previous = Environment.GetEnvironmentVariable("ConnectionStrings__WmsDatabase");
        try
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__WmsDatabase", connection);
            using var context = new WmsDbContextFactory().CreateDbContext([]);
            Assert.That(context.Database.GetDbConnection().Database, Is.EqualTo(expectedDatabase));
            Assert.That(context.Database.ProviderName, Is.EqualTo("Npgsql.EntityFrameworkCore.PostgreSQL"));
        }
        finally { Environment.SetEnvironmentVariable("ConnectionStrings__WmsDatabase", previous); }
    }

    [Test]
    public void Every_committed_migration_can_generate_a_complete_rollback_script_without_a_database()
    {
        using var context = new WmsDbContext(new DbContextOptionsBuilder<WmsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unit;Username=unit").Options);
        var sql = context.GetService<IMigrator>().GenerateScript("20261026170941_AddAuthenticationSessions", Migration.InitialDatabase);
        Assert.That(sql, Does.Contain("DROP TABLE"));
        Assert.That(sql, Does.Contain("AuthSessions"));
        Assert.That(sql, Does.Contain("StockBalances"));
    }

    private static IServiceCollection Register(Dictionary<string, string?> settings) =>
        new ServiceCollection().AddLogging().AddAuthenticationInfrastructure(Config(settings));
    private static IConfiguration Config(Dictionary<string, string?> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Jwt:SigningKeyBase64"] = Key,
        ["ConnectionStrings:Redis"] = "127.0.0.1:6379"
    };
}
