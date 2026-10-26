using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;
using MiniWms.Application.Authentication;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Persistence;
using Npgsql;
using NUnit.Framework;
using StackExchange.Redis;
using Role = MiniWms.Domain.Entities.Role;

namespace MiniWms.Api.IntegrationTests;

[TestFixture]
[NonParallelizable]
public sealed partial class AuthApiIntegrationTests
{
    private readonly Guid adminId = Guid.NewGuid();
    private readonly Guid staffId = Guid.NewGuid();
    private readonly Guid inactiveId = Guid.NewGuid();
    private readonly string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string redisPrefix = "miniwms:test:" + Guid.NewGuid().ToString("N") + ":";
    private readonly string signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    private string adminConnection = string.Empty;
    private string databaseName = string.Empty;
    private string databaseConnection = string.Empty;
    private Dictionary<string, string?> configuration = new();
    private AuthApiFactory? factory;
    private HttpClient? client;
    private AuthApiFactory Factory => factory ?? throw new InvalidOperationException("Test host is not initialized.");
    private HttpClient Client => client ?? throw new InvalidOperationException("Test client is not initialized.");
    private IDatabase Redis => Factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    [OneTimeSetUp]
    public async Task CreateIsolatedDatabaseAndHost()
    {
        var configuredPostgres = Environment.GetEnvironmentVariable("MINIWMS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredPostgres))
        {
            Assert.Ignore("Real PostgreSQL/Redis auth tests require MINIWMS_TEST_POSTGRES; use scripts/Test-AuthIntegration.ps1.");
            return;
        }
        var connection = new NpgsqlConnectionStringBuilder(configuredPostgres) { Database = "postgres" };
        adminConnection = connection.ConnectionString;
        databaseName = "miniwms_auth_tests_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        connection.Database = databaseName;
        databaseConnection = connection.ConnectionString;
        await using (var schema = CreateContext()) { await schema.Database.MigrateAsync(); }
        configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:WmsDatabase"] = databaseConnection,
            ["ConnectionStrings:Redis"] = Environment.GetEnvironmentVariable("MINIWMS_TEST_REDIS") ?? "127.0.0.1:6379",
            ["Redis:KeyPrefix"] = redisPrefix,
            ["Jwt:Issuer"] = "MiniWms.IntegrationTests",
            ["Jwt:Audience"] = "MiniWms.IntegrationClient",
            ["Jwt:SigningKeyBase64"] = signingKey,
            ["Authentication:AccessTokenMinutes"] = "15",
            ["Authentication:SessionLifetimeHours"] = "8",
            ["Authentication:LoginPermitLimit"] = "10000"
        };
        factory = new AuthApiFactory(configuration);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using (var scope = Factory.Services.CreateScope())
        {
            Assert.That(scope.ServiceProvider.GetRequiredService<WmsDbContext>().Database.GetDbConnection().Database, Is.EqualTo(databaseName));
        }
        Assert.That(await Redis.PingAsync(), Is.GreaterThanOrEqualTo(TimeSpan.Zero));
    }

    [SetUp]
    public async Task ResetOnlyTheIsolatedFixture()
    {
        if (factory is null) { return; }
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"RefreshTokens\", \"AuthSessions\", \"Users\", \"Roles\", \"Products\", \"Warehouses\" CASCADE");
        var adminRole = new Role { Name = "Admin" };
        var staffRole = new Role { Name = "Staff" };
        var hasher = new PasswordHasher<User>();
        User CreateUser(Guid id, string email, Role role, bool active)
        {
            var user = new User { UserId = id, FullName = "Integration account", Email = email, RoleId = role.RoleId, Role = role, IsActive = active };
            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }
        context.Roles.AddRange(adminRole, staffRole);
        context.Users.AddRange(CreateUser(adminId, "admin@integration.test", adminRole, true),
            CreateUser(staffId, "staff@integration.test", staffRole, true),
            CreateUser(inactiveId, "inactive@integration.test", staffRole, false));
        await context.SaveChangesAsync();
        foreach (var id in new[] { adminId, staffId, inactiveId }) { await Redis.KeyDeleteAsync(SessionKey(id)); }
    }

    [OneTimeTearDown]
    public async Task RemoveOnlyTheIsolatedDatabase()
    {
        if (factory is not null)
        {
            try
            {
                foreach (var id in new[] { adminId, staffId, inactiveId }) { await Redis.KeyDeleteAsync(SessionKey(id)); }
            }
            finally
            {
                client?.Dispose();
                await factory.DisposeAsync();
            }
        }
        if (databaseName.Length != "miniwms_auth_tests_".Length + 32 ||
            !databaseName.StartsWith("miniwms_auth_tests_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(databaseName["miniwms_auth_tests_".Length..], "N", out _)) { return; }
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private WmsDbContext CreateContext()
    {
        var builder = new NpgsqlConnectionStringBuilder(databaseConnection);
        if (string.IsNullOrEmpty(databaseName) || builder.Database != databaseName ||
            !databaseName.StartsWith("miniwms_auth_tests_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to access a database outside this isolated fixture.");
        }
        return new WmsDbContext(new DbContextOptionsBuilder<WmsDbContext>().UseNpgsql(databaseConnection).Options);
    }

    private RedisKey SessionKey(Guid userId) => redisPrefix + "session:{" + userId.ToString("N") + "}";

    private async Task<AuthTokens> LoginAsync(string email = "staff@integration.test", HttpClient? target = null)
    {
        using var response = await (target ?? Client).PostAsJsonAsync("/auth/login", new { email, password });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Login must succeed without logging credentials.");
        return await response.Content.ReadFromJsonAsync<AuthTokens>() ?? throw new InvalidOperationException("Missing token response.");
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient target, string refreshToken) =>
        target.PostAsJsonAsync("/auth/refresh", new { refreshToken });

    private static async Task<HttpResponseMessage> MeAsync(HttpClient target, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await target.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ChangeBanAsync(HttpClient target, string accessToken, Guid userId, bool banned)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/staff/{userId:D}/{(banned ? "ban" : "unban")}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await target.SendAsync(request);
    }

    private sealed class AuthApiFactory(
        IReadOnlyDictionary<string, string?> settings,
        Action<IServiceCollection>? customize = null) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Minimal-host startup reads configuration while registering services. Host
            // configuration reaches WebApplication.CreateBuilder before those reads.
            builder.ConfigureHostConfiguration(configurationBuilder => configurationBuilder.AddInMemoryCollection(settings));
            return base.CreateHost(builder);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(settings));
            builder.ConfigureLogging(logging => logging.ClearProviders());
            if (customize is not null) { builder.ConfigureServices(customize); }
        }
    }
}
